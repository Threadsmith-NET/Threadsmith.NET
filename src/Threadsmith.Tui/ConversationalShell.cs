namespace Threadsmith.Tui;

using System.ComponentModel;
using System.Diagnostics;
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
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Identifies why a composer interaction completed.</summary>
internal enum ConsoleInputKind
{
    /// <summary>The composer submitted user text.</summary>
    Submission,

    /// <summary>The user requested a reasoning-visibility toggle.</summary>
    ToggleThinking,
}

/// <summary>Represents one completed or cancelled composer interaction.</summary>
/// <param name="IsSubmitted">Whether the user submitted the input.</param>
/// <param name="Text">The submitted text.</param>
/// <param name="OperationCancellationToken">Token that cancels processing started from this input.</param>
/// <param name="Kind">Reason the interaction completed.</param>
internal sealed record ConsoleInput(
    bool IsSubmitted,
    string Text,
    CancellationToken OperationCancellationToken,
    ConsoleInputKind Kind = ConsoleInputKind.Submission);

/// <summary>Abstracts interactive terminal input and output for deterministic shell tests.</summary>
internal interface IConsoleSurface
{
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
        TuiSessionStatus status,
        string separator,
        CancellationToken cancellationToken = default)
    {
        var text = TuiSessionStatusFormatter.Format(status, 120, separator);
        return string.IsNullOrEmpty(text)
            ? Task.CompletedTask
            : WriteAsync(text + Environment.NewLine, TuiTextRole.SessionStatus, cancellationToken);
    }

    /// <summary>Reads one multiline composer submission.</summary>
    /// <param name="cancellationToken">Stops the pending read.</param>
    /// <returns>The completed composer interaction.</returns>
    Task<ConsoleInput> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Lets the user choose one item with arrow keys and Enter.</summary>
    /// <param name="title">Prompt displayed above the choices.</param>
    /// <param name="choices">Numbered choice labels.</param>
    /// <param name="cancellationToken">Stops the pending selection.</param>
    /// <returns>The zero-based selected choice index.</returns>
    Task<int> SelectAsync(
        string title,
        IReadOnlyList<string> choices,
        CancellationToken cancellationToken = default);

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
        TuiActivity activity,
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
        TuiTextRole role = TuiTextRole.Default,
        CancellationToken cancellationToken = default);

    /// <summary>Writes ordered mixed-role output into native terminal scrollback.</summary>
    /// <param name="segments">Terminal-neutral semantic segments.</param>
    /// <param name="cancellationToken">Stops the pending write.</param>
    async Task WriteSegmentsAsync(
        IReadOnlyList<TuiTextSegment> segments,
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
        IReadOnlyList<TuiOutputItem> items,
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
}

/// <summary>Adapts PrettyPrompt input and Spectre.Console output to the terminal surface.</summary>
internal sealed class PrettyPromptConsoleSurface : IConsoleSurface
{
    private const double _completionPaneHeightFraction = 0.01;
    private const string ToggleThinkingInput = "threadsmith:toggle-thinking";
    private const int _completionPaneVerticalBorderRows = 2;

    private static readonly FieldInfo _minimumCompletionItemsField = typeof(PromptConfiguration).GetField(
        "<MinCompletionItemsCount>k__BackingField",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("PrettyPrompt no longer exposes its completion-pane minimum field.");

    private readonly SemaphoreSlim _consoleGate = new(1, 1);
    private readonly IAnsiConsole _ansiConsole;
    private readonly bool _isOutputRedirected;
    private readonly TextWriter _redirectedOutput;
    private readonly bool _suppressStyles;

    private string _promptText;
    private IPrompt? _prompt;
    private TuiThemeResolver _themeResolver;

    /// <summary>Initializes a new instance of the <see cref="PrettyPromptConsoleSurface"/> class.</summary>
    internal PrettyPromptConsoleSurface(
        ConfiguredTheme? initialTheme = null,
        bool? isOutputRedirected = null,
        IAnsiConsole? ansiConsole = null)
    {
        _ansiConsole = ansiConsole ?? AnsiConsole.Console;
        _isOutputRedirected = isOutputRedirected ?? Console.IsOutputRedirected;
        _redirectedOutput = _ansiConsole.Profile.Out.Writer;
        var suppressionReason = TuiThemeResolver.GetSuppressionReason(
            _isOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"));
        _suppressStyles = suppressionReason is not null;
        var activeTheme = initialTheme ?? BuiltInThemes.Create()[0];
        _themeResolver = new TuiThemeResolver(activeTheme.Theme, _suppressStyles);
        Debug.WriteLine(
            $"Threadsmith TUI theme '{activeTheme.Theme.Id}' selected; "
            + $"style fallback: {suppressionReason ?? "none"}.");
        _promptText = "threadsmith > ";
    }

    /// <inheritdoc />
    public async Task SetPromptAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
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
        TuiSessionStatus status,
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
            : WriteAsync(text + Environment.NewLine, TuiTextRole.SessionStatus, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ConsoleInput> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            var prompt = _prompt ??= CreatePrompt(_promptText);
            var result = await prompt.ReadLineAsync().WaitAsync(cancellationToken);
            var toggledThinking = result is KeyPressCallbackResult
                && string.Equals(result.Text, ToggleThinkingInput, StringComparison.Ordinal);
            return new ConsoleInput(
                result.IsSuccess,
                toggledThinking ? string.Empty : result.Text,
                result.CancellationToken,
                toggledThinking ? ConsoleInputKind.ToggleThinking : ConsoleInputKind.Submission);
        }
        finally
        {
            _consoleGate.Release();
        }
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
            var promptStyle = ToMarkupStyle(_themeResolver.Resolve(TuiTextRole.SelectionPrompt));
            var itemStyle = ToMarkupStyle(_themeResolver.Resolve(TuiTextRole.SelectionItem));
            string[] numberedChoices = [.. choices.Select((choice, index) =>
                ApplyMarkupStyle($"{index + 1}. {Markup.Escape(choice)}", itemStyle))];
            var prompt = new SelectionPrompt<string>()
                .Title(ApplyMarkupStyle(Markup.Escape(title), promptStyle))
                .EnableSearch()
                .SearchPlaceholderText("Type a number or use Up/Down")
                .AddChoices(numberedChoices);
            prompt.HighlightStyle = ToSpectreStyle(_themeResolver.Resolve(TuiTextRole.SelectionHighlight));
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
                ? TuiTextRole.ThinkingIndicator
                : TuiTextRole.Status;
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
        TuiActivity activity,
        Task operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(operation);
        await _consoleGate.WaitAsync(cancellationToken);
        try
        {
            var statusRole = activity.Label.StartsWith("THINKING", StringComparison.Ordinal)
                ? TuiTextRole.ThinkingIndicator
                : TuiTextRole.Status;
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
        TuiTextRole role = TuiTextRole.Default,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return WriteSegmentsAsync([new TuiTextSegment(text, role)], cancellationToken);
    }

    /// <inheritdoc />
    public Task WriteSegmentsAsync(
        IReadOnlyList<TuiTextSegment> segments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        return WriteOutputAsync([new TuiSegmentOutput(segments)], cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteOutputAsync(
        IReadOnlyList<TuiOutputItem> items,
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
            if (projected is TuiRawSourceOutput rawSourceOutput)
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

    private sealed record SegmentConsoleSurfaceWrite(TuiTextSegment Segment) : ConsoleSurfaceWrite;

    private sealed record RawConsoleSurfaceWrite(string RawSource) : ConsoleSurfaceWrite;

    /// <summary>Projects a source-bearing item through the immutable redirected-output capability.</summary>
    /// <param name="item">Output item to admit.</param>
    /// <param name="isOutputRedirected">Whether the output sink is proven non-terminal.</param>
    /// <returns>A raw item only for a proven redirected sink; otherwise the original safe item.</returns>
    internal static TuiOutputItem ProjectOutputItem(TuiOutputItem item, bool isOutputRedirected)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            TuiMarkdownOutput markdownOutput when isOutputRedirected =>
                new TuiRawSourceOutput(markdownOutput.RawSource),
            TuiSourceOutput sourceOutput when isOutputRedirected =>
                new TuiRawSourceOutput(sourceOutput.RawSource),
            TuiRawSourceOutput when !isOutputRedirected => throw new InvalidOperationException(
                "Raw source output cannot be admitted by an interactive console surface."),
            _ => item,
        };
    }

    /// <summary>Projects one safe output item for interactive terminal presentation.</summary>
    /// <param name="item">Safe output item to project.</param>
    /// <param name="width">Available terminal display-cell width.</param>
    /// <returns>Validated terminal-neutral segments, including answer-block separation.</returns>
    internal static IReadOnlyList<TuiTextSegment> ProjectInteractiveOutputItem(
        TuiOutputItem item,
        int width)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item switch
        {
            TuiSegmentOutput segmentOutput => segmentOutput.Segments,
            TuiSourceOutput sourceOutput => PrefixAnswerBlock(
                [new TuiTextSegment(sourceOutput.SafeSource, TuiTextRole.Default)],
                sourceOutput.StartsAnswerBlock),
            TuiMarkdownOutput markdownOutput => PrefixAnswerBlock(
                ProjectMarkdownOutput(markdownOutput, width),
                markdownOutput.StartsAnswerBlock),
            TuiRawSourceOutput => throw new InvalidOperationException(
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
    internal static IReadOnlyList<TuiTextSegment> PrefixAnswerBlock(
        IReadOnlyList<TuiTextSegment> segments,
        bool startsAnswerBlock)
    {
        return startsAnswerBlock
                ? [new TuiTextSegment("\n", TuiTextRole.Default), .. segments]
                : segments;
    }

    /// <summary>Projects one Markdown answer for interactive terminal layout.</summary>
    /// <param name="markdownOutput">Validated semantic document and terminal-safe source fallback.</param>
    /// <param name="width">Available terminal display-cell width.</param>
    /// <returns>Terminal-neutral interactive segments.</returns>
    internal static IReadOnlyList<TuiTextSegment> ProjectMarkdownOutput(
        TuiMarkdownOutput markdownOutput,
        int width)
    {
        ArgumentNullException.ThrowIfNull(markdownOutput);
        try
        {
            return TuiMarkdownLayout.Format(markdownOutput.Document, width);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return [new TuiTextSegment(markdownOutput.SafeSource, TuiTextRole.Default)];
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
            or RunCompleted
            || (domainEvent is ModelOutputObserved && emittedModelOutput);
    }

    /// <summary>Refreshes transient activity until its operation completes or display cancellation is observed.</summary>
    internal static async Task RefreshActivityUntilCompletedAsync(
        TuiActivity activity,
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
        var promptStyle = _themeResolver.Resolve(TuiTextRole.ComposerPrompt);
        var configuration = new PromptConfiguration(
            prompt: new FormattedString(prompt, ToPrettyPromptFormat(promptStyle)),
            proportionOfWindowHeightForCompletionPane: _completionPaneHeightFraction);

        // The composer has no completion source. PrettyPrompt's public configuration always reserves
        // two borders plus one completion row, so clear that unused minimum to retain the full window height.
        _minimumCompletionItemsField.SetValue(configuration, -_completionPaneVerticalBorderRows);
        return new Prompt(
            callbacks: new ThinkingPromptCallbacks(),
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
    internal sealed class ThinkingPromptCallbacks : PromptCallbacks
    {
        /// <inheritdoc />
        protected override IEnumerable<(KeyPressPattern Pattern, KeyPressCallbackAsync Callback)>
            GetKeyPressCallbacks()
        {
            yield return (
                new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.T),
                (text, _, _) => Task.FromResult<KeyPressCallbackResult?>(
                    text.Length == 0
                        ? new KeyPressCallbackResult(ToggleThinkingInput, output: null)
                        : null));
        }
    }
}

/// <summary>Classifies host approval events for the interactive review queue.</summary>
internal static class InteractiveDecisionClassifier
{
    /// <summary>Returns whether an approval request represents an interactive structured-plan review.</summary>
    /// <param name="requested">Approval request to classify.</param>
    /// <returns><see langword="true" /> when the request is a plan-review prompt.</returns>
    public static bool IsPlanApprovalRequest(ApprovalRequested requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        return requested.Kind == ApprovalRequestKind.Plan;
    }
}

/// <summary>Identifies an interactive host decision requested by a domain event.</summary>
internal enum InteractiveDecisionKind
{
    /// <summary>A structured plan requires review.</summary>
    Plan,

    /// <summary>A staged mutation requires review.</summary>
    Mutation,
}

/// <summary>Represents a pending review transferred from event rendering to the input loop.</summary>
/// <param name="Kind">Kind of review.</param>
/// <param name="MutationSetId">Mutation set when the review concerns a mutation.</param>
/// <param name="ApprovalId">Approval identity when the review concerns a mutation.</param>
/// <param name="RequiredApproval">Host-classified approval mode for the staged mutation.</param>
internal sealed record InteractiveDecision(
    InteractiveDecisionKind Kind,
    MutationSetId? MutationSetId = null,
    ApprovalId? ApprovalId = null,
    MutationApprovalLevel RequiredApproval = MutationApprovalLevel.EntireSet);

/// <summary>Result of consuming one interactive host decision.</summary>
internal enum InteractiveDecisionResult
{
    /// <summary>The run remains foregrounded and waits for more events.</summary>
    ContinueWaiting,

    /// <summary>The run is waiting for a staged mutation review.</summary>
    AwaitingMutationReview,
}

/// <summary>
/// Runs Threadsmith as an inline conversational terminal whose transcript remains in
/// native terminal scrollback.
/// </summary>
public sealed class ConversationalShell
{
    private const string StartupBanner = """
         _____ _                        _               _ _   _
        |_   _| |__  _ __ ___  __ _  __| |___ _ __ ___ (_) |_| |__
          | | | '_ \| '__/ _ \/ _` |/ _` / __| '_ ` _ \| | __| '_ \
          | | | | | | | |  __/ (_| | (_| \__ \ | | | | | | |_| | | |
          |_| |_| |_|_|  \___|\__,_|\__,_|___/_| |_| |_|_|\__|_| |_|
                                                              .NET
        Forge better code, not slop.
        """;

    private readonly IDomainEventStream _events;
    private readonly bool _activeModelSelectionAvailable;
    private readonly bool _sessionLifecycleAvailable;
    private readonly IClaudeSkillCompatibilityCatalog? _claudeSkills;
    private readonly ConfiguredModelCatalog? _modelCatalog;
    private readonly ModelProfileId? _startupProfileId;
    private readonly TuiPresenter _presenter;
    private readonly SessionModelPreferences? _sessionPreferences;
    private readonly SessionThemePreferences _themePreferences;
    private readonly IThemePreferenceStore? _themePreferenceStore;
    private readonly IReadOnlyList<string> _displayWarnings;
    private readonly TuiDisplayOptions _displayOptions;
    private readonly IReadOnlyList<MutationValidationStage> _validationStages;
    private readonly IReadOnlyList<string> _themeWarnings;
    private readonly TimeProvider _timeProvider;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly bool _showSessionStatus;
    private readonly IConsoleSurface _surface;
    private readonly IExtensionManager? _extensionManager;
    private readonly IMutationApprovalPolicy? _mutationApprovalPolicy;
    private readonly IPlanApprovalPolicy? _planApprovalPolicy;
    private readonly IToolStateManager? _toolStateManager;
    private readonly IGitQueryService? _gitQueries;
    private readonly WebFetchAuthorizationAuthority? _webFetchAuthorization;
    private readonly DirectFetchApprovalPromptRouter? _directFetchApprovalPrompt;

    /// <summary>Initializes a new instance of the <see cref="ConversationalShell"/> class.</summary>
    /// <param name="presenter">Command and projection adapter.</param>
    /// <param name="events">Live domain-event source.</param>
    /// <param name="modelCatalog">Configured model catalog, or <see langword="null"/> when no model is configured.</param>
    /// <param name="activeProfileId">Active model profile id, or <see langword="null"/> when no model is selected.</param>
    /// <param name="sessionPreferences">Mutable session model preferences, or <see langword="null"/> in scripted tests.</param>
    /// <param name="extensionManager">The extension manager for the /extensions command, or null when extensions are disabled.</param>
    /// <param name="configuration">Effective layered configuration used for TUI theme and footer selection.</param>
    /// <param name="sessionUsage">Provider-neutral session usage projection.</param>
    /// <param name="toolStateManager">Repository-scoped tool availability state for the /tools command.</param>
    /// <param name="mutationApprovalPolicy">Session mutation approval policy for the /policy command.</param>
    /// <param name="planApprovalPolicy">Session plan approval policy for the /plan-policy command.</param>
    /// <param name="activeModelSelectionAvailable">Whether active-model commands are composed.</param>
    /// <param name="claudeSkills">Claude-style compatibility catalog, or null when unavailable.</param>
    /// <param name="sessionLifecycleAvailable">Whether durable active-session commands are composed.</param>
    /// <param name="gitQueries">Local Git query boundary used for status projection.</param>
    /// <param name="webFetchAuthorization">Transient exact-URL authorization boundary.</param>
    /// <param name="directFetchApprovalPrompt">Serialized inline approval boundary.</param>
    /// <param name="userConfigurationPath">Ordinary user configuration path used to persist theme selection.</param>
    /// <param name="validationStages">Host-owned resolved validation stages for post-apply status projection.</param>
    public ConversationalShell(
        TuiPresenter presenter,
        IDomainEventStream events,
        ConfiguredModelCatalog? modelCatalog = null,
        ModelProfileId? activeProfileId = null,
        SessionModelPreferences? sessionPreferences = null,
        IExtensionManager? extensionManager = null,
        IConfiguration? configuration = null,
        SessionUsageProjection? sessionUsage = null,
        IToolStateManager? toolStateManager = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IPlanApprovalPolicy? planApprovalPolicy = null,
        bool activeModelSelectionAvailable = false,
        IClaudeSkillCompatibilityCatalog? claudeSkills = null,
        bool sessionLifecycleAvailable = false,
        IGitQueryService? gitQueries = null,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null,
        string? userConfigurationPath = null,
        IReadOnlyList<MutationValidationStage>? validationStages = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(events);
        (var catalog, var defaultThemeId) = TuiThemeConfigurationLoader.Load(configuration);
        _themePreferences = new SessionThemePreferences(catalog, defaultThemeId);
        _themeWarnings = catalog.Warnings;
        _displayOptions = TuiDisplayOptions.Load(configuration);
        _validationStages = validationStages ?? [];
        _displayWarnings = _displayOptions.Diagnostics;
        _timeProvider = TimeProvider.System;
        _presenter = presenter;
        _events = events;
        _surface = new PrettyPromptConsoleSurface(_themePreferences.ActiveTheme);
        _modelCatalog = modelCatalog;
        _startupProfileId = activeProfileId;
        _sessionPreferences = sessionPreferences;
        _extensionManager = extensionManager;
        _sessionUsage = sessionUsage;
        _toolStateManager = toolStateManager;
        _mutationApprovalPolicy = mutationApprovalPolicy;
        _planApprovalPolicy = planApprovalPolicy;
        _activeModelSelectionAvailable = activeModelSelectionAvailable;
        _claudeSkills = claudeSkills;
        _sessionLifecycleAvailable = sessionLifecycleAvailable;
        _gitQueries = gitQueries;
        _webFetchAuthorization = webFetchAuthorization;
        _directFetchApprovalPrompt = directFetchApprovalPrompt;
        _themePreferenceStore = string.IsNullOrWhiteSpace(userConfigurationPath)
            ? null
            : new UserConfigurationThemePreferenceStore(userConfigurationPath);
        _showSessionStatus = configuration?.GetValue("tui:footer:enabled", true) ?? true;
        foreach (var warning in catalog.Warnings)
        {
            Debug.WriteLine(warning);
        }
    }

    /// <summary>Initializes a new instance of the <see cref="ConversationalShell"/> class with an injected surface.</summary>
    /// <param name="presenter">Command and projection adapter.</param>
    /// <param name="events">Live domain-event source.</param>
    /// <param name="surface">Terminal input/output adapter.</param>
    /// <param name="modelCatalog">Configured model catalog, or <see langword="null"/> when no model is configured.</param>
    /// <param name="activeProfileId">Active model profile id, or <see langword="null"/> when no model is selected.</param>
    /// <param name="sessionPreferences">Mutable session model preferences, or <see langword="null"/> in scripted tests.</param>
    /// <param name="extensionManager">The extension manager for the /extensions command, or null when extensions are disabled.</param>
    /// <param name="themePreferences">Process-local theme state, or null to use the system theme.</param>
    /// <param name="sessionUsage">Provider-neutral session usage projection.</param>
    /// <param name="showSessionStatus">Whether to render the composer-adjacent status row.</param>
    /// <param name="toolStateManager">Repository-scoped tool availability state for the /tools command.</param>
    /// <param name="mutationApprovalPolicy">Session mutation approval policy for the /policy command.</param>
    /// <param name="planApprovalPolicy">Session plan approval policy for the /plan-policy command.</param>
    /// <param name="activeModelSelectionAvailable">Whether active-model commands are composed.</param>
    /// <param name="claudeSkills">Claude-style compatibility catalog, or null when unavailable.</param>
    /// <param name="sessionLifecycleAvailable">Whether durable active-session commands are composed.</param>
    /// <param name="displayOptions">Immutable operation-duration display options.</param>
    /// <param name="timeProvider">Clock used for monotonic transient activity timing.</param>
    /// <param name="gitQueries">Local Git query boundary used for status projection.</param>
    /// <param name="webFetchAuthorization">Transient exact-URL authorization boundary.</param>
    /// <param name="directFetchApprovalPrompt">Serialized inline approval boundary.</param>
    /// <param name="themePreferenceStore">Optional user-level theme persistence boundary.</param>
    /// <param name="validationStages">Host-owned resolved validation stages for post-apply status projection.</param>
    internal ConversationalShell(
        TuiPresenter presenter,
        IDomainEventStream events,
        IConsoleSurface surface,
        ConfiguredModelCatalog? modelCatalog = null,
        ModelProfileId? activeProfileId = null,
        SessionModelPreferences? sessionPreferences = null,
        IExtensionManager? extensionManager = null,
        SessionThemePreferences? themePreferences = null,
        SessionUsageProjection? sessionUsage = null,
        bool showSessionStatus = true,
        IToolStateManager? toolStateManager = null,
        IMutationApprovalPolicy? mutationApprovalPolicy = null,
        IPlanApprovalPolicy? planApprovalPolicy = null,
        bool activeModelSelectionAvailable = false,
        IClaudeSkillCompatibilityCatalog? claudeSkills = null,
        bool sessionLifecycleAvailable = false,
        TuiDisplayOptions? displayOptions = null,
        TimeProvider? timeProvider = null,
        IGitQueryService? gitQueries = null,
        WebFetchAuthorizationAuthority? webFetchAuthorization = null,
        DirectFetchApprovalPromptRouter? directFetchApprovalPrompt = null,
        IThemePreferenceStore? themePreferenceStore = null,
        IReadOnlyList<MutationValidationStage>? validationStages = null)
    {
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(surface);
        _presenter = presenter;
        _events = events;
        _surface = surface;
        _modelCatalog = modelCatalog;
        _startupProfileId = activeProfileId;
        _sessionPreferences = sessionPreferences;
        _extensionManager = extensionManager;
        _sessionUsage = sessionUsage;
        _showSessionStatus = showSessionStatus;
        _toolStateManager = toolStateManager;
        _mutationApprovalPolicy = mutationApprovalPolicy;
        _planApprovalPolicy = planApprovalPolicy;
        _activeModelSelectionAvailable = activeModelSelectionAvailable;
        _claudeSkills = claudeSkills;
        _sessionLifecycleAvailable = sessionLifecycleAvailable;
        _displayOptions = displayOptions ?? new TuiDisplayOptions();
        _validationStages = validationStages ?? [];
        _displayWarnings = _displayOptions.Diagnostics;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gitQueries = gitQueries;
        _webFetchAuthorization = webFetchAuthorization;
        _directFetchApprovalPrompt = directFetchApprovalPrompt;
        _themePreferenceStore = themePreferenceStore;
        _themePreferences = themePreferences ?? new SessionThemePreferences(
            new ConfiguredThemeCatalog(BuiltInThemes.Create()),
            "system");
        _themeWarnings = _themePreferences.Catalog.Warnings;
    }

    /// <summary>Runs the interactive conversation until the user quits or cancellation is requested.</summary>
    /// <param name="repositoryPath">Repository opened before the first composer prompt.</param>
    /// <param name="requestedTrust">Optional trust supplied by the command line.</param>
    /// <param name="requestedSolutionPath">Optional solution supplied by the command line.</param>
    /// <param name="modelStatus">Effective default model shown in startup status.</param>
    /// <param name="repositoryConfigurationDirectoryExistedAtStartup">
    /// Whether the repository configuration directory existed before host runtime storage was initialized.
    /// </param>
    /// <param name="cancellationToken">Stops the interactive session.</param>
    public async Task RunAsync(
        string? repositoryPath = null,
        RepositoryTrustLevel? requestedTrust = null,
        string? requestedSolutionPath = null,
        string modelStatus = "Scripted demo (offline)",
        bool? repositoryConfigurationDirectoryExistedAtStartup = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelStatus);
        var controller = new TuiController(_presenter);
        var sessionId = _sessionLifecycleAvailable
            ? (await controller.CreateNewSessionAsync(cancellationToken)).ActiveSession.SessionId
            : await controller.OpenAsync("Interactive", cancellationToken);
        var dispatcher = new UiEventDispatcher();
        var decisions = Channel.CreateUnbounded<InteractiveDecision>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var directFetchApprovalLease = _directFetchApprovalPrompt?.Attach(
            PromptForDirectFetchApprovalAsync,
            dispatcher.QueueAsync);
        var transcript = new ConversationTranscript(
            string.Empty,
            _displayOptions.ShowOperationDurations);
        await using var subscription = _events.Subscribe(dispatcher.QueueAsync);
        var semanticCompletion = new TaskCompletionSource<SemanticLoadCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var semanticCompletionSubscription = _events.Subscribe((domainEvent, _) =>
        {
            if (domainEvent is SemanticLoadCompleted completion
                && completion.SessionId == sessionId)
            {
                semanticCompletion.TrySetResult(completion);
            }

            return Task.CompletedTask;
        });

        await _surface.WriteAsync(
            StartupBanner + Environment.NewLine + Environment.NewLine,
            TuiTextRole.Brand,
            lifetime.Token);
        foreach (var warning in _themeWarnings.Concat(_displayWarnings))
        {
            await _surface.WriteAsync(
                $"Warning: {warning}{Environment.NewLine}{Environment.NewLine}",
                TuiTextRole.Warning,
                lifetime.Token);
        }

        RepositoryOpenWorkflowResult? activeRepository = null;
        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            activeRepository = await OpenRepositoryAsync(
                controller,
                repositoryPath,
                requestedTrust,
                requestedSolutionPath,
                repositoryConfigurationDirectoryExistedAtStartup,
                lifetime.Token);

            var startupWasCancelled = activeRepository.Repository is null
                || (activeRepository.Repository.Trust.Level >= RepositoryTrustLevel.TrustedRead
                    && activeRepository.Repository.SolutionCandidates.Count > 1
                    && activeRepository.Solution is null);
            if (startupWasCancelled)
            {
                await _surface.WriteAsync(
                    "Startup cancelled.\n",
                    TuiTextRole.Status,
                    lifetime.Token);
                dispatcher.Complete();
                decisions.Writer.TryComplete();
                return;
            }
        }

        SemanticLoadCompleted? startupSemanticCompletion = null;
        if (activeRepository?.Solution is not null)
        {
            try
            {
                await _surface.ShowStatusUntilAsync(
                    "Semantic confidence: Loading...",
                    semanticCompletion.Task,
                    lifetime.Token);
                startupSemanticCompletion = await semanticCompletion.Task;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Ctrl+C during the startup semantic-confidence spinner is a clean exit,
                // not an error. Tear down the dispatcher and decision channel (the drain
                // task has not been started yet) and return without surfacing a stack trace.
                await _surface.WriteAsync(
                    "Startup cancelled.\n",
                    TuiTextRole.Status,
                    CancellationToken.None);
                dispatcher.Complete();
                decisions.Writer.TryComplete();
                return;
            }
        }

        var snapshot = await controller.RenderAsync(lifetime.Token);
        var repositoryStatus = activeRepository?.Repository?.RepositoryPath
            ?? snapshot.RepositoryPath
            ?? "Not open";
        var trustStatus = activeRepository?.Repository?.Trust.Level.ToString()
            ?? snapshot.RepositoryTrust?.ToString()
            ?? "Not granted";
        var solutionStatus = activeRepository?.Solution?.SolutionPath
            ?? snapshot.SolutionPath
            ?? "Not selected";
        var hasSelectedSolution = activeRepository?.Solution is not null
            || snapshot.SolutionPath is not null;
        var semanticConfidence = snapshot.SemanticConfidence;
        if (startupSemanticCompletion is not null
            && Enum.TryParse<SemanticConfidenceLevel>(
                startupSemanticCompletion.Confidence,
                ignoreCase: false,
                out var completedConfidence))
        {
            semanticConfidence = completedConfidence;
        }

        var initialSemanticWasResolved = startupSemanticCompletion is not null
            || snapshot.IsSemanticLoadComplete
            || semanticConfidence != SemanticConfidenceLevel.None;
        var semanticStatus = initialSemanticWasResolved
            && semanticConfidence == SemanticConfidenceLevel.None
            ? "Unavailable"
            : !initialSemanticWasResolved && hasSelectedSolution
            ? "Loading..."
            : semanticConfidence.ToString();
        var startupStatus = new StringBuilder()
            .AppendLine()
            .AppendLine("Current status")
            .AppendLine($"  Model: {modelStatus}")
            .AppendLine($"  Repository: {repositoryStatus}")
            .AppendLine($"  Trust: {trustStatus}")
            .AppendLine($"  Solution: {solutionStatus}");
        if (snapshot.TargetFrameworks is { Count: > 0 } targetFrameworks)
        {
            startupStatus.AppendLine($"  Target frameworks: {string.Join(", ", targetFrameworks)}");
        }

        startupStatus.AppendLine($"  Semantic confidence: {semanticStatus}")
            .AppendLine("  Mode: Interactive")
            .AppendLine(_showSessionStatus
                ? "  Session status: Composer-adjacent (fixed footer unavailable through PrettyPrompt public APIs)"
                : "  Session status: Disabled by tui:footer:enabled")
            .AppendLine();
        await _surface.WriteAsync(
            startupStatus.ToString(),
            TuiTextRole.Status,
            lifetime.Token);
        if (activeRepository?.Repository is { } startupRepository)
        {
            await SetRepositoryPromptAsync(startupRepository.RepositoryPath, lifetime.Token);
        }

        var startupCompletedAt = DateTimeOffset.UtcNow;
        TaskCompletionSource? activityCompletion = null;
        TaskCompletionSource? renderedRunCompletion = null;
        Task? activityDisplayTask = null;
        TuiActivity? currentActivity = null;
        SemanticActivityKey? currentSemanticActivityKey = null;
        var semanticActivitiesByKey = new Dictionary<SemanticActivityKey, TuiActivity>();
        var semanticActivityOrder = new List<SemanticActivityKey>();
        long? turnStartedTimestamp = null;
        var reasoningExpanded = false;
        ContextInspectionProjection? latestContextInspection = null;
        var modelAnswerCollector = new TuiModelAnswerCollector(_displayOptions.RenderMarkdown);

        var drainTask = dispatcher.DrainAsync(
            async (batch, token) =>
            {
                var output = new List<TuiOutputItem>();
                var pendingDecisions = new List<InteractiveDecision>();
                var runCompletedInBatch = false;
                var nextActivity = currentActivity;
                var nextSemanticActivityKey = currentSemanticActivityKey;
                foreach (var domainEvent in batch.Where(item => item.SessionId == sessionId))
                {
                    var occurredDuringStartup = domainEvent.OccurredAt <= startupCompletedAt;
                    if (occurredDuringStartup
                        && (domainEvent is RepositoryOpened or SolutionLoaded
                            || (initialSemanticWasResolved
                                && (domainEvent is SemanticConfidenceChanged or SemanticLoadCompleted))))
                    {
                        continue;
                    }

                    if (domainEvent is TaskIntentRecorded)
                    {
                        turnStartedTimestamp = _timeProvider.GetTimestamp();
                    }

                    var directFetchApprovalRequested = domainEvent is DirectFetchApprovalPromptStarted;
                    var directFetchApprovalGranted = domainEvent is DirectFetchApprovalPromptCompleted
                    {
                        Outcome: DirectFetchApprovalOutcome.Approved,
                    };
                    var directFetchApprovalDenied = domainEvent is DirectFetchApprovalPromptCompleted
                    {
                        Outcome: not DirectFetchApprovalOutcome.Approved,
                    };
                    SemanticActivityKey? incomingSemanticActivityKey = null;
                    TuiActivity? incomingActivity;
                    if (domainEvent is SemanticCheckStarted semanticStarted)
                    {
                        var semanticKey = new SemanticActivityKey(
                            semanticStarted.RunId,
                            semanticStarted.SemanticCheckId);
                        incomingActivity = new TuiActivity(
                            FormatActiveSemanticCheckLabel(semanticStarted),
                            _timeProvider.GetTimestamp(),
                            _displayOptions.ShowOperationDurations,
                            _timeProvider);
                        if (!semanticActivitiesByKey.ContainsKey(semanticKey))
                        {
                            semanticActivityOrder.Add(semanticKey);
                        }

                        semanticActivitiesByKey[semanticKey] = incomingActivity;
                        incomingSemanticActivityKey = semanticKey;
                    }
                    else
                    {
                        incomingActivity = domainEvent switch
                        {
                            TaskIntentRecorded or ModelReasoningObserved when turnStartedTimestamp is { } turnStart =>
                                new TuiActivity(
                                    "THINKING",
                                    turnStart,
                                    _displayOptions.ShowOperationDurations,
                                    _timeProvider),
                            ToolInvocationStarted started => new TuiActivity(
                                FormatActiveToolLabel(started),
                                _timeProvider.GetTimestamp(),
                                _displayOptions.ShowOperationDurations,
                                _timeProvider),
                            MutationProposalStarted => new TuiActivity(
                                "MUTATION PREVIEW",
                                _timeProvider.GetTimestamp(),
                                _displayOptions.ShowOperationDurations,
                                _timeProvider),
                            _ when directFetchApprovalGranted => new TuiActivity(
                                "TOOLS: web_fetch",
                                _timeProvider.GetTimestamp(),
                                _displayOptions.ShowOperationDurations,
                                _timeProvider),
                            _ => null,
                        };
                    }

                    var previousLength = transcript.Text.Length;
                    var transcriptDelta = transcript.Apply(domainEvent)
                        ? transcript.Text[previousLength..]
                        : string.Empty;
                    var emittedModelOutput = false;
                    if (domainEvent is ModelOutputObserved)
                    {
                        var modelOutput = modelAnswerCollector.Append(transcriptDelta);
                        if (modelOutput is not null)
                        {
                            output.Add(modelOutput);
                            emittedModelOutput = true;
                        }
                    }
                    else
                    {
                        var pendingAnswer = modelAnswerCollector.Flush(token);
                        if (pendingAnswer is not null)
                        {
                            output.Add(pendingAnswer);
                        }

                        var eventSegments = new List<TuiTextSegment>();
                        TuiEventSegments.Append(eventSegments, domainEvent, transcriptDelta);
                        if (eventSegments.Count > 0)
                        {
                            output.Add(new TuiSegmentOutput(eventSegments));
                        }
                    }

                    var activityEnded = PrettyPromptConsoleSurface.EndsTransientActivity(
                            domainEvent,
                            emittedModelOutput)
                        || directFetchApprovalRequested
                        || directFetchApprovalDenied;
                    if (incomingActivity is not null)
                    {
                        nextActivity = incomingActivity;
                        nextSemanticActivityKey = incomingSemanticActivityKey;
                    }
                    else if (domainEvent is SemanticCheckCompleted semanticCompleted)
                    {
                        var semanticKey = new SemanticActivityKey(
                            semanticCompleted.RunId,
                            semanticCompleted.SemanticCheckId);
                        _ = semanticActivitiesByKey.Remove(semanticKey);
                        semanticActivityOrder.Remove(semanticKey);
                        if (nextSemanticActivityKey == semanticKey)
                        {
                            if (TryGetLatestSemanticActivity(
                                semanticActivitiesByKey,
                                semanticActivityOrder,
                                out var latestSemanticKey,
                                out var latestSemanticActivity))
                            {
                                nextActivity = latestSemanticActivity;
                                nextSemanticActivityKey = latestSemanticKey;
                            }
                            else
                            {
                                nextActivity = null;
                                nextSemanticActivityKey = null;
                            }
                        }
                    }
                    else if (domainEvent is ToolInvocationCompleted && turnStartedTimestamp is { } continuationStart)
                    {
                        nextActivity = new TuiActivity(
                            "THINKING",
                            continuationStart,
                            _displayOptions.ShowOperationDurations,
                            _timeProvider);
                        nextSemanticActivityKey = null;
                    }
                    else if (activityEnded)
                    {
                        nextActivity = null;
                        nextSemanticActivityKey = null;
                    }

                    if (domainEvent is RunCompleted completed)
                    {
                        RemoveSemanticActivitiesForRun(
                            semanticActivitiesByKey,
                            semanticActivityOrder,
                            completed.RunId);
                        turnStartedTimestamp = null;
                    }

                    switch (domainEvent)
                    {
                        case ContextAssembled assembled:
                            latestContextInspection = assembled.Inspection;
                            break;
                        case ApprovalRequested requested when InteractiveDecisionClassifier.IsPlanApprovalRequest(requested):
                            pendingDecisions.Add(new InteractiveDecision(InteractiveDecisionKind.Plan));
                            break;
                        case MutationSetProposed proposed:
                            pendingDecisions.Add(new InteractiveDecision(
                                InteractiveDecisionKind.Mutation,
                                proposed.MutationSetId,
                                proposed.ApprovalId,
                                proposed.RequiredApproval));
                            break;
                        case RunCompleted:
                            runCompletedInBatch = true;
                            break;
                    }
                }

                try
                {
                    if (activityCompletion is not null
                        && (output.Count > 0 || currentActivity != nextActivity))
                    {
                        activityCompletion.TrySetResult();
                        var completedActivityDisplayTask = activityDisplayTask;
                        activityCompletion = null;
                        activityDisplayTask = null;
                        currentActivity = null;
                        currentSemanticActivityKey = null;
                        if (completedActivityDisplayTask is not null)
                        {
                            await completedActivityDisplayTask;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    await WriteCancellationSafeOutputAsync(_surface, output);
                    throw;
                }

                if (output.Count > 0)
                {
                    await WriteOutputWithCancellationFallbackAsync(_surface, output, token);
                }

                if (nextActivity is not null && activityCompletion is null)
                {
                    activityCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    currentActivity = nextActivity;
                    currentSemanticActivityKey = nextSemanticActivityKey;
                    activityDisplayTask = _surface.ShowActivityUntilAsync(
                        nextActivity,
                        activityCompletion.Task,
                        token);
                }

                if (runCompletedInBatch)
                {
                    renderedRunCompletion?.TrySetResult();
                }

                foreach (var decision in pendingDecisions)
                {
                    decisions.Writer.TryWrite(decision);
                }
            },
            lifetime.Token);

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                while (decisions.Reader.TryRead(out var pendingDecision))
                {
                    _ = await HandleDecisionAsync(controller, pendingDecision, lifetime.Token);
                }

                ConsoleInput input;
                try
                {
                    if (_showSessionStatus)
                    {
                        var activePath = activeRepository?.Repository?.RepositoryPath
                            ?? snapshot.RepositoryPath
                            ?? Directory.GetCurrentDirectory();
                        var repositoryName = activeRepository?.Repository is null
                            && snapshot.RepositoryPath is null
                            ? "Not open"
                            : TuiSessionStatusFactory.GetRepositoryDisplayName(activePath);
                        var currentProfileId = _sessionPreferences?.CurrentProfileId
                            ?? _startupProfileId;
                        var currentProfile = currentProfileId is { } profileId
                            && _modelCatalog is not null
                            ? _modelCatalog.Profiles.FirstOrDefault(profile => profile.Id == profileId)
                            : null;
                        var usage = _sessionUsage?.GetSnapshot(sessionId)
                            ?? new SessionUsageSnapshot(0, 0, false, HasObservation: false);
                        var branch = await ResolveCurrentBranchAsync(
                            _gitQueries,
                            activePath,
                            repositoryName != "Not open",
                            lifetime.Token);
                        var status = TuiSessionStatusFactory.Create(
                            Directory.GetCurrentDirectory(),
                            repositoryName,
                            modelStatus,
                            currentProfile,
                            _sessionPreferences?.Reasoning ?? ReasoningLevel.None,
                            latestContextInspection,
                            usage,
                            branch);
                        await _surface.ShowSessionStatusAsync(
                            status,
                            _themePreferences.ActiveTheme.Ui.FooterSeparator,
                            lifetime.Token);
                    }

                    input = await _surface.ReadAsync(lifetime.Token);
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                    break;
                }

                if (!input.IsSubmitted)
                {
                    await _surface.WriteAsync(
                        "Input cancelled. Enter /quit to exit.\n",
                        TuiTextRole.Status,
                        lifetime.Token);
                    continue;
                }

                var submittedText = input.Text;
                var commandText = submittedText.Trim();
                var toggleThinking = input.Kind == ConsoleInputKind.ToggleThinking
                    || string.Equals(commandText, "/thinking", StringComparison.OrdinalIgnoreCase);
                if (toggleThinking)
                {
                    var reasoning = transcript.LatestReasoning;
                    if (string.IsNullOrWhiteSpace(reasoning))
                    {
                        await _surface.WriteAsync(
                            "No reasoning is available for the latest response.\n",
                            TuiTextRole.Status,
                            lifetime.Token);
                    }
                    else
                    {
                        reasoningExpanded = !reasoningExpanded;
                        var visibilityOutput = reasoningExpanded
                            ? $"<thinking>{Environment.NewLine}{reasoning}{Environment.NewLine}</thinking>{Environment.NewLine}"
                            : $"THINKING collapsed.{Environment.NewLine}";
                        await _surface.WriteAsync(
                            visibilityOutput,
                            reasoningExpanded ? TuiTextRole.Reasoning : TuiTextRole.Muted,
                            lifetime.Token);
                    }

                    continue;
                }

                if (string.IsNullOrEmpty(commandText))
                {
                    continue;
                }

                if (string.Equals(commandText, "/quit", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (string.Equals(commandText, "/help", StringComparison.OrdinalIgnoreCase))
                {
                    await _surface.WriteAsync(
                        FormatHelpText(),
                        TuiTextRole.Status,
                        lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/validation", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 11 || char.IsWhiteSpace(commandText[11])))
                {
                    var argument = commandText.Length == 11
                        ? string.Empty
                        : commandText[11..].Trim();
                    if (!string.Equals(argument, "retry", StringComparison.OrdinalIgnoreCase))
                    {
                        await _surface.WriteAsync(
                            "Usage: /validation retry\n",
                            TuiTextRole.Error,
                            lifetime.Token);
                        continue;
                    }

                    if (controller.BackgroundValidationRunId is not { } validationRunId)
                    {
                        await _surface.WriteAsync(
                            "No interrupted post-apply validation is awaiting retry.\n",
                            TuiTextRole.Status,
                            lifetime.Token);
                        continue;
                    }

                    _ = await StartPostApplyValidationAsync(
                        controller,
                        validationRunId,
                        lifetime.Token);
                    continue;
                }

                if (_sessionLifecycleAvailable
                    && string.Equals(commandText, "/new", StringComparison.OrdinalIgnoreCase))
                {
                    var result = await controller.CreateNewSessionAsync(lifetime.Token);
                    _webFetchAuthorization?.RevokeAll();
                    sessionId = result.ActiveSession.SessionId;
                    latestContextInspection = null;
                    reasoningExpanded = false;
                    snapshot = await controller.RenderAsync(lifetime.Token);
                    await _surface.WriteAsync(
                        $"Threadsmith: New session {sessionId.Value:D}.\n",
                        TuiTextRole.Status,
                        lifetime.Token);
                    continue;
                }

                if (_sessionLifecycleAvailable
                    && string.Equals(commandText, "/clone", StringComparison.OrdinalIgnoreCase))
                {
                    var result = await controller.CloneSessionAsync(lifetime.Token);
                    _webFetchAuthorization?.RevokeAll();
                    sessionId = result.ActiveSession.SessionId;
                    latestContextInspection = null;
                    reasoningExpanded = false;
                    snapshot = await controller.RenderAsync(lifetime.Token);
                    await _surface.WriteAsync(
                        $"Threadsmith: Cloned session {result.SourceSessionId?.Value:D} as {sessionId.Value:D}.\n"
                        + $"/resume {result.SourceSessionId?.Value:D}\n",
                        TuiTextRole.Status,
                        lifetime.Token);
                    continue;
                }

                if (_sessionLifecycleAvailable
                    && commandText.StartsWith("/resume", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 7 || char.IsWhiteSpace(commandText[7])))
                {
                    var argument = commandText.Length == 7 ? string.Empty : commandText[7..].Trim();
                    SessionId target;
                    if (argument.Length == 0)
                    {
                        var sessions = await controller.ListResumableSessionsAsync(
                            cancellationToken: lifetime.Token);
                        if (sessions.Count == 0)
                        {
                            await _surface.WriteAsync(
                                "No resumable sessions exist for this repository.\n",
                                TuiTextRole.Status,
                                lifetime.Token);
                            continue;
                        }

                        string[] labels = [.. sessions.Select(entry => FormatSessionChoice(entry, sessionId))];
                        var selected = await _surface.SelectAsync(
                            "Resume session",
                            labels,
                            lifetime.Token);
                        target = sessions[selected].SessionId;
                    }
                    else if (Guid.TryParse(argument, out var parsedSessionId))
                    {
                        target = new SessionId(parsedSessionId);
                    }
                    else
                    {
                        await _surface.WriteAsync(
                            "Usage: /resume [session-id]\n",
                            TuiTextRole.Error,
                            lifetime.Token);
                        continue;
                    }

                    var result = await controller.ResumeSessionAsync(target, lifetime.Token);
                    _webFetchAuthorization?.RevokeAll();
                    sessionId = result.ActiveSession.SessionId;
                    latestContextInspection = null;
                    reasoningExpanded = false;
                    snapshot = await controller.RenderAsync(lifetime.Token);
                    var warnings = result.Warnings.Count == 0
                        ? string.Empty
                        : "\nWarning: " + string.Join(" ", result.Warnings);
                    await _surface.WriteAsync(
                        $"Threadsmith: Resumed session {sessionId.Value:D}.{warnings}\n",
                        result.Warnings.Count == 0 ? TuiTextRole.Status : TuiTextRole.Warning,
                        lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/auth", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 5 || char.IsWhiteSpace(commandText[5])))
                {
                    var arguments = commandText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (arguments.Length is < 2 or > 3
                        || !string.Equals(arguments[1], "openai-codex", StringComparison.OrdinalIgnoreCase)
                        || (arguments.Length == 3
                            && !Enum.TryParse(
                                arguments[2],
                                ignoreCase: true,
                                out ModelProviderAuthenticationAction _)))
                    {
                        await _surface.WriteAsync(
                            "Usage: /auth openai-codex [login|status|logout]\n",
                            TuiTextRole.Error,
                            lifetime.Token);
                        continue;
                    }

                    var action = arguments.Length == 3
                        ? Enum.Parse<ModelProviderAuthenticationAction>(arguments[2], ignoreCase: true)
                        : ModelProviderAuthenticationAction.Login;
                    var result = await _presenter
                        .ManageModelProviderAuthenticationAsync("openai-codex", action, lifetime.Token);
                    var resultRole = result.IsAuthenticated
                        || action == ModelProviderAuthenticationAction.Logout
                            ? TuiTextRole.Status
                            : TuiTextRole.Error;
                    await _surface.WriteAsync(
                        result.Message + "\n",
                        resultRole,
                        lifetime.Token);
                    continue;
                }

                if (string.Equals(commandText, "/models", StringComparison.OrdinalIgnoreCase))
                {
                    if (await ManageModelsAsync(lifetime.Token))
                    {
                        latestContextInspection = null;
                    }

                    continue;
                }

                if (string.Equals(commandText, "/extensions", StringComparison.OrdinalIgnoreCase))
                {
                    await ManageExtensionsAsync(controller, lifetime.Token);
                    continue;
                }

                if (string.Equals(commandText, "/tools", StringComparison.OrdinalIgnoreCase))
                {
                    await ManageToolsAsync(lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/fetch-authorize", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 16 || char.IsWhiteSpace(commandText[16])))
                {
                    await HandleFetchAuthorizationCommandAsync(
                        commandText,
                        activeRepository?.Repository?.RepositoryPath,
                        sessionId,
                        lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 4 || char.IsWhiteSpace(commandText[4])))
                {
                    await HandleMcpCommandAsync(controller, commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/hooks", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 6 || char.IsWhiteSpace(commandText[6])))
                {
                    await HandleHooksCommandAsync(
                        controller,
                        activeRepository?.Repository?.RepositoryPath,
                        commandText,
                        lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/skills", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 7 || char.IsWhiteSpace(commandText[7])))
                {
                    var skillTrust = activeRepository?.Repository?.Trust.Level
                        ?? RepositoryTrustLevel.UntrustedInspection;
                    await HandleSkillsCommandAsync(
                        controller,
                        sessionId,
                        skillTrust,
                        commandText,
                        lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/context", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 8 || char.IsWhiteSpace(commandText[8])))
                {
                    await HandleContextCommandAsync(controller, commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/agents", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 7 || char.IsWhiteSpace(commandText[7])))
                {
                    await HandleAgentsCommandAsync(controller, commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/policy", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 7 || char.IsWhiteSpace(commandText[7])))
                {
                    await HandlePolicyCommandAsync(commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/plan-policy", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 12 || char.IsWhiteSpace(commandText[12])))
                {
                    await HandlePlanPolicyCommandAsync(controller, commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/theme", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 6 || char.IsWhiteSpace(commandText[6])))
                {
                    await HandleThemeCommandAsync(commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith("/open", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 5 || char.IsWhiteSpace(commandText[5])))
                {
                    var openPath = commandText.Length == 5
                        ? string.Empty
                        : commandText[5..].Trim();
                    if (string.IsNullOrWhiteSpace(openPath))
                    {
                        await _surface.WriteAsync(
                            "Repository path:\n",
                            TuiTextRole.Status,
                            lifetime.Token);
                        var pathInput = await _surface.ReadAsync(lifetime.Token);
                        openPath = pathInput.IsSubmitted ? pathInput.Text.Trim() : string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(openPath))
                    {
                        await _surface.WriteAsync(
                            "Repository open cancelled.\n",
                            TuiTextRole.Status,
                            lifetime.Token);
                        continue;
                    }

                    try
                    {
                        var result = await OpenRepositoryAsync(
                            controller,
                            openPath,
                            requestedTrust: null,
                            requestedSolutionPath: null,
                            configurationDirectoryExistedBeforeRuntimeStorage: null,
                            lifetime.Token);
                        activeRepository = result.Repository is null
                            ? activeRepository
                            : result;
                        if (result.Repository is { } openedRepository)
                        {
                            await SetRepositoryPromptAsync(
                                openedRepository.RepositoryPath,
                                lifetime.Token);
                        }

                        var status = result.Repository is null
                            ? "Repository open cancelled."
                            : result.Repository.SolutionCandidates.Count > 1
                                && result.Solution is null
                                ? "Repository opened; solution selection cancelled."
                                : $"Repository opened ({result.Repository.Trust.Level}).";
                        await _surface.WriteAsync(
                            status + Environment.NewLine,
                            TuiTextRole.Status,
                            lifetime.Token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await _surface.WriteAsync(
                            FormatStatusError(exception) + Environment.NewLine,
                            TuiTextRole.Error,
                            lifetime.Token);
                    }

                    continue;
                }

                if (commandText.StartsWith("/trust", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 6 || char.IsWhiteSpace(commandText[6])))
                {
                    if (activeRepository?.Repository is not { } openRepository)
                    {
                        await _surface.WriteAsync(
                            "No repository is open. Use /open [path] first.\n",
                            TuiTextRole.Error,
                            lifetime.Token);
                        continue;
                    }

                    var trustText = commandText.Length == 6
                        ? string.Empty
                        : commandText[6..].Trim();
                    var trust = string.IsNullOrWhiteSpace(trustText)
                        ? await ReadTrustAsync(lifetime.Token)
                        : ParseInteractiveTrust(trustText);
                    if (trust is null)
                    {
                        await _surface.WriteAsync(
                            "Trust change cancelled or invalid. Enter /help for supported levels.\n",
                            TuiTextRole.Error,
                            lifetime.Token);
                        continue;
                    }

                    try
                    {
                        var previousTrust = openRepository.Trust.Level;
                        var result = await OpenRepositoryAsync(
                            controller,
                            openRepository.RepositoryPath,
                            trust,
                            activeRepository.Solution?.SolutionPath,
                            configurationDirectoryExistedBeforeRuntimeStorage: null,
                            lifetime.Token);
                        var updatedRepository = result.Repository ?? openRepository;
                        activeRepository = result.Repository is null ? activeRepository : result;
                        var effectiveTrust = updatedRepository.Trust.Level;
                        var status = effectiveTrust == previousTrust && trust < previousTrust
                            ? $"Trust remains {effectiveTrust}; persisted trust cannot be downgraded."
                            : $"Repository trust is now {effectiveTrust}.";
                        await _surface.WriteAsync(
                            status + Environment.NewLine,
                            TuiTextRole.Status,
                            lifetime.Token);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        await _surface.WriteAsync(
                            FormatStatusError(exception) + Environment.NewLine,
                            TuiTextRole.Error,
                            lifetime.Token);
                    }

                    continue;
                }

                if (commandText.StartsWith("/reasoning", StringComparison.OrdinalIgnoreCase)
                    && (commandText.Length == 10 || char.IsWhiteSpace(commandText[10])))
                {
                    await HandleReasoningCommandAsync(commandText, lifetime.Token);
                    continue;
                }

                if (commandText.StartsWith('/'))
                {
                    await _surface.WriteAsync(
                        $"Unknown command: {commandText}. Enter /help for commands.\n",
                        TuiTextRole.Error,
                        lifetime.Token);
                    continue;
                }

                using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    lifetime.Token,
                    input.OperationCancellationToken);
                try
                {
                    await EnsureCurrentUserUrlConsentAsync(submittedText, operation.Token);
                    renderedRunCompletion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    reasoningExpanded = false;
                    _ = await controller.SubmitAsync(submittedText, operation.Token);
                    var waitTask = controller.WaitForActiveRunAsync(operation.Token);
                    var awaitingMutationReview = false;
                    while (!waitTask.IsCompleted || awaitingMutationReview)
                    {
                        if (waitTask.IsFaulted)
                        {
                            _ = await waitTask;
                        }

                        var decisionAvailableTask = decisions.Reader
                            .WaitToReadAsync(operation.Token)
                            .AsTask();
                        if (!awaitingMutationReview)
                        {
                            var completed = await Task.WhenAny(
                                waitTask,
                                decisionAvailableTask,
                                drainTask);
                            if (completed == drainTask)
                            {
                                await drainTask;
                            }

                            if (completed == waitTask)
                            {
                                break;
                            }
                        }
                        else
                        {
                            var completed = await Task.WhenAny(decisionAvailableTask, drainTask);
                            if (completed == drainTask)
                            {
                                await drainTask;
                            }
                        }

                        if (!await decisionAvailableTask)
                        {
                            break;
                        }

                        while (decisions.Reader.TryRead(out var decision))
                        {
                            var decisionResult = await HandleDecisionAsync(
                                controller,
                                decision,
                                operation.Token);
                            awaitingMutationReview = decisionResult == InteractiveDecisionResult.AwaitingMutationReview;
                        }
                    }

                    _ = await waitTask;
                    var renderingTask = renderedRunCompletion.Task.WaitAsync(operation.Token);
                    var completedTask = await Task.WhenAny(renderingTask, drainTask);
                    if (completedTask == drainTask)
                    {
                        await drainTask;
                    }

                    await renderingTask;
                    renderedRunCompletion = null;
                }
                catch (OperationCanceledException) when (operation.IsCancellationRequested)
                {
                    _ = await controller.CancelActiveRunAsync(CancellationToken.None);
                    await _surface.WriteAsync(
                        "Run cancelled.\n",
                        TuiTextRole.Status,
                        CancellationToken.None);
                }
                catch (Exception exception) when (!drainTask.IsFaulted)
                {
                    await _surface.WriteAsync(
                        FormatStatusError(exception) + Environment.NewLine,
                        TuiTextRole.Error,
                        lifetime.Token);
                }
            }
        }
        finally
        {
            if (controller.ActiveRunId is not null)
            {
                _ = await controller.CancelActiveRunAsync(CancellationToken.None);
            }

            activityCompletion?.TrySetResult();
            renderedRunCompletion?.TrySetResult();
            var finalActivityDisplayTask = activityDisplayTask ?? Task.CompletedTask;
            dispatcher.Complete();
            decisions.Writer.TryComplete();
            try
            {
                await Task.WhenAll(finalActivityDisplayTask, drainTask);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                // Session cancellation intentionally ends the event-drain loop.
            }
            finally
            {
                try
                {
                    var finalAnswer = FlushFinalAnswerForShutdown(modelAnswerCollector);
                    if (finalAnswer is not null)
                    {
                        await _surface.WriteOutputAsync([finalAnswer], CancellationToken.None);
                    }
                }
                finally
                {
                    await lifetime.CancelAsync();
                }
            }
        }
    }

    /// <summary>Writes one batch, terminalizing source with a non-cancelled token if admission is cancelled.</summary>
    /// <param name="surface">Serialized console surface.</param>
    /// <param name="output">Ordered output batch.</param>
    /// <param name="cancellationToken">Cancels admission to the surface gate.</param>
    internal static async Task WriteOutputWithCancellationFallbackAsync(
        IConsoleSurface surface,
        IReadOnlyList<TuiOutputItem> output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(output);
        try
        {
            await surface.WriteOutputAsync(output, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteCancellationSafeOutputAsync(surface, output);
            throw;
        }
    }

    /// <summary>Converts semantic documents to source and writes the complete batch without cancellation.</summary>
    /// <param name="surface">Serialized console surface.</param>
    /// <param name="output">Ordered output batch.</param>
    internal static Task WriteCancellationSafeOutputAsync(
        IConsoleSurface surface,
        IReadOnlyList<TuiOutputItem> output)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(output);
        TuiOutputItem[] safeOutput =
        [
            .. output.Select(item => item is TuiMarkdownOutput markdownOutput
                ? new TuiSourceOutput(
                    markdownOutput.RawSource,
                    markdownOutput.SafeSource,
                    markdownOutput.StartsAnswerBlock)
                : item),
        ];
        return safeOutput.Length == 0
            ? Task.CompletedTask
            : surface.WriteOutputAsync(safeOutput, CancellationToken.None);
    }

    /// <summary>Flushes an incomplete shutdown answer through cancellation-safe source projection.</summary>
    /// <param name="collector">Answer collector owned by the current shell projection.</param>
    /// <returns>One terminal-safe source item, or <see langword="null"/> when no answer remains.</returns>
    internal static TuiOutputItem? FlushFinalAnswerForShutdown(TuiModelAnswerCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        return collector.Flush(new CancellationToken(canceled: true));
    }

    /// <summary>Formats the user-visible post-apply validation start when no semantic activity will follow.</summary>
    /// <param name="stages">Configured validation stages.</param>
    /// <returns>A shared lifecycle block, or <see langword="null" /> when semantic checks provide activity.</returns>
    internal static string? FormatPostApplyValidationStart(IReadOnlyList<MutationValidationStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        return stages.Count > 0 && !stages.Contains(MutationValidationStage.Semantic)
            ? TuiPresentationFormatter.FormatMutationValidationStarted(stages)
            : null;
    }

    /// <summary>Formats the user-visible post-apply validation start into mixed semantic roles.</summary>
    /// <param name="stages">Configured validation stages.</param>
    /// <returns>A shared lifecycle block as semantic segments, or <see langword="null" /> when semantic checks provide activity.</returns>
    internal static IReadOnlyList<TuiTextSegment>? FormatPostApplyValidationStartSegments(
        IReadOnlyList<MutationValidationStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var startMessage = FormatPostApplyValidationStart(stages);
        if (startMessage is null)
        {
            return null;
        }

        var segments = new List<TuiTextSegment>();
        TuiEventSegments.AppendLifecycleBlock(segments, startMessage, TuiTextRole.Status);
        return segments;
    }

    /// <summary>Formats the user-visible post-apply validation outcome.</summary>
    /// <param name="phase">Execution phase returned by post-apply validation.</param>
    /// <param name="suffix">Optional formatted duration suffix including leading space.</param>
    /// <returns>The status line and semantic role to display.</returns>
    internal static (string Message, TuiTextRole Role) FormatPostApplyValidationResult(
        ExecutionCheckpointPhase phase,
        string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);
        return phase switch
        {
            ExecutionCheckpointPhase.Completed => ($"Validation completed{suffix}.\n", TuiTextRole.Status),
            ExecutionCheckpointPhase.MutationApprovalPending => (
                $"Validation requires a correction review{suffix}.\n",
                TuiTextRole.Warning),
            ExecutionCheckpointPhase.Failed => (
                $"Validation failed{suffix}; mutation was not accepted.\n",
                TuiTextRole.Error),
            ExecutionCheckpointPhase.Cancelled => (
                $"Validation cancelled{suffix}; mutation was not accepted.\n",
                TuiTextRole.Warning),
            ExecutionCheckpointPhase.RolledBack => (
                $"Validation rolled back the applied mutation{suffix}.\n",
                TuiTextRole.Warning),
            _ => (
                $"Validation stopped at {phase}{suffix}; mutation acceptance is unresolved.\n",
                TuiTextRole.Warning),
        };
    }

    /// <summary>Resolves the current branch for status display, or null when Git is unavailable.</summary>
    /// <param name="gitQueries">Git query boundary, or null when unavailable.</param>
    /// <param name="repositoryPath">Opened repository path.</param>
    /// <param name="repositoryIsOpen">Whether the repository is open.</param>
    /// <param name="cancellationToken">Stops the branch query.</param>
    /// <returns>The current branch, or null for detached or unavailable Git state.</returns>
    internal static async Task<string?> ResolveCurrentBranchAsync(
        IGitQueryService? gitQueries,
        string repositoryPath,
        bool repositoryIsOpen,
        CancellationToken cancellationToken)
    {
        if (gitQueries is null || !repositoryIsOpen)
        {
            return null;
        }

        try
        {
            return await gitQueries.GetCurrentBranchAsync(repositoryPath, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>Validates and grants one interactive exact URL chain without allowing invalid input to escape.</summary>
    /// <param name="commandText">Complete host command text.</param>
    /// <param name="repositoryRoot">Active repository root, or null when none is open.</param>
    /// <param name="sessionId">Active session identity.</param>
    /// <param name="cancellationToken">Stops command output.</param>
    internal async Task HandleFetchAuthorizationCommandAsync(
        string commandText,
        string? repositoryRoot,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandText);
        var directUrls = commandText.Length == 16 ? string.Empty : commandText[16..].Trim();
        var authorizationChain = directUrls.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (_webFetchAuthorization is null
            || string.IsNullOrWhiteSpace(repositoryRoot)
            || authorizationChain.Length == 0)
        {
            await _surface.WriteAsync(
                "Usage: /fetch-authorize <initial-public-https-url> [redirect-public-https-url ...]\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var maximumUrlCount = _webFetchAuthorization.MaximumDirectUrlCount;
        if (authorizationChain.Length > maximumUrlCount)
        {
            await _surface.WriteAsync(
                $"A direct fetch chain accepts at most {maximumUrlCount} URLs under current repository limits.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        try
        {
            _webFetchAuthorization.GrantDirectUrlChain(repositoryRoot, sessionId, authorizationChain);
            await _surface.WriteAsync(
                "Authorized one exact invocation-bound URL chain for one web_fetch invocation.\n",
                TuiTextRole.Status,
                cancellationToken);
        }
        catch (WebFetchException exception)
        {
            await _surface.WriteAsync(exception.Message + "\n", TuiTextRole.Error, cancellationToken);
        }
        catch (ArgumentException)
        {
            await _surface.WriteAsync(
                "The direct fetch authorization chain is outside current repository limits.\n",
                TuiTextRole.Error,
                cancellationToken);
        }
    }

    private async Task EnsureCurrentUserUrlConsentAsync(
        string rawMessage,
        CancellationToken cancellationToken)
    {
        if (_toolStateManager is null
            || !CurrentUserUrlRecognizer.HasEligibleCandidate(rawMessage)
            || !_toolStateManager.IsEnabled("web_fetch")
            || !_toolStateManager.RequiresCurrentMessageUrlConsent())
        {
            return;
        }

        var decision = await _surface.SelectAsync(
            "Threadsmith may send search terms to the configured provider, retrieve selected results, and contact an exact public HTTPS URL in your current request only if the model invokes web_fetch. Model-proposed destinations require separate inline approval. Fetched content is untrusted and supplied to the model. Enable this revised repository-bound consent?",
            ["No — continue without current-message URL authority", "Yes — enable the documented one-shot route"],
            cancellationToken);
        if (decision == 1)
        {
            await _toolStateManager.GrantConsentAndEnableAsync(
                "web_fetch",
                retrievalDisclosureAcknowledged: true,
                currentMessageUrlDisclosureAcknowledged: true,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<DirectFetchApprovalOutcome> PromptForDirectFetchApprovalAsync(
        DirectFetchApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = string.IsNullOrEmpty(request.Path) ? "/" : request.Path;
        var query = request.QueryPresent
            ? "present (values hidden)"
            : "absent";
        var decision = await _surface.SelectAsync(
            "The model proposed a public web destination that is not user-authored or search-result-authorized. "
            + $"Origin: {request.Origin}; path: {path}; query: {query}; exact digest: {request.UrlDigest}. "
            + "Approval permits one credential-free attempt for this invocation only and does not authorize redirects or the origin.",
            ["Deny", "Approve one attempt"],
            cancellationToken);
        return decision == 1
            ? DirectFetchApprovalOutcome.Approved
            : DirectFetchApprovalOutcome.Denied;
    }

    private async Task<bool> ManageModelsAsync(CancellationToken cancellationToken)
    {
        if (!_activeModelSelectionAvailable)
        {
            await _surface.WriteAsync(
                "Model selection is not available in this session.\n",
                TuiTextRole.Status,
                cancellationToken);
            return false;
        }

        var current = await _presenter.GetActiveModelSelectionAsync(cancellationToken);
        var models = await _presenter.ListActiveModelsAsync(cancellationToken);
        string[] choices = [.. models.Select(entry =>
        {
            var marker = entry.Profile.Id == current.Profile.Id ? "*" : " ";
            var reasoning = entry.Profile.ReasoningCapability.Controllability switch
            {
                ReasoningControllability.Selectable => string.Join(
                    '/',
                    entry.Profile.SupportedReasoningLevels.Select(level => level.ToString().ToLowerInvariant())),
                ReasoningControllability.AlwaysOn => "always-on",
                _ => "unsupported",
            };
            return $"{marker} {entry.Profile.Name} — {entry.ProviderName} ({entry.ProviderId})"
                + $" — context {entry.Profile.ContextWindow:N0}, output {entry.Profile.MaximumOutputTokens:N0}"
                + $" — reasoning {reasoning}";
        })];
        var selected = await _surface.SelectAsync(
            "Models (Up/Down, Enter; Esc to cancel):",
            choices,
            cancellationToken);
        if (selected < 0 || selected >= models.Count)
        {
            return false;
        }

        var result = await _presenter.SelectActiveModelAsync(
            models[selected].Profile.Id,
            cancellationToken);
        var selection = result.Selection;
        await _surface.WriteAsync(
            $"Model set to {selection.Profile.Name} ({selection.ProviderId}); "
            + $"context {selection.Profile.ContextWindow:N0}; reasoning "
            + $"{selection.ReasoningLevel.ToString().ToLowerInvariant()}.\n",
            result.Persisted ? TuiTextRole.Status : TuiTextRole.Error,
            cancellationToken);
        if (!result.ReasoningPreserved)
        {
            var choicesText = string.Join(
                ", ",
                selection.Profile.SupportedReasoningLevels
                    .Select(level => level.ToString().ToLowerInvariant()));
            await _surface.WriteAsync(
                "The selected model does not support an equivalent reasoning level. Reasoning was set to none.\n"
                + $"Use /reasoning <level> to select one of: {choicesText}.\n",
                TuiTextRole.Status,
                cancellationToken);
        }

        if (!result.Persisted)
        {
            await _surface.WriteAsync(
                (result.Diagnostic ?? "Repository model persistence failed.") + Environment.NewLine,
                TuiTextRole.Error,
                cancellationToken);
        }

        return result.Changed;
    }

    private async Task ManageToolsAsync(CancellationToken cancellationToken)
    {
        if (_toolStateManager is null)
        {
            await _surface.WriteAsync(
                "Tool management is not available in this session.\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var states = _toolStateManager.GetAllStates();
            string[] choices =
            [
                .. states.Select(FormatToolChoice),
                "Back",
            ];
            var selected = await _surface.SelectAsync(
                "Tools (Up/Down, Enter; Back to return):",
                choices,
                cancellationToken);
            if (selected < 0 || selected >= states.Count)
            {
                return;
            }

            var state = states[selected];
            if (state.Essential)
            {
                await _surface.WriteAsync(
                    $"{state.DisplayName} is essential and cannot be disabled.{Environment.NewLine}",
                    TuiTextRole.Status,
                    cancellationToken);
                continue;
            }

            try
            {
                if (state.Enabled)
                {
                    await _toolStateManager.DisableAsync(state.Id, cancellationToken);
                }
                else
                {
                    if (state.ConsentRequired)
                    {
                        var confirmation = await _surface.SelectAsync(
                            "Web Search may send query text to the configured provider. Selected results may be retrieved. An exact public HTTPS URL in your current request may be contacted only if the model invokes web_fetch; model-proposed destinations require separate inline approval. Fetched content is untrusted and supplied to the model. Grant this repository-bound consent?",
                            ["No — keep disabled", "Yes — grant consent and enable"],
                            cancellationToken);
                        if (confirmation != 1)
                        {
                            continue;
                        }

                        await _toolStateManager.GrantConsentAndEnableAsync(
                            state.Id,
                            retrievalDisclosureAcknowledged: true,
                            currentMessageUrlDisclosureAcknowledged: true,
                            cancellationToken: cancellationToken);
                    }
                    else
                    {
                        await _toolStateManager.EnableAsync(state.Id, cancellationToken);
                    }
                }

                await _surface.WriteAsync(
                    $"{state.DisplayName} {(state.Enabled ? "disabled" : "enabled")}.{Environment.NewLine}",
                    TuiTextRole.Status,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await _surface.WriteAsync(
                    $"Tool availability update failed: {exception.Message}{Environment.NewLine}",
                    TuiTextRole.Error,
                    cancellationToken);
            }
        }
    }

    private static string FormatHelpText()
    {
        const int descriptionColumn = 42;
        (string Command, string Description)[] entries =
        [
            ("/open [path]", "Open a repository and choose trust"),
            ("/trust [inspect|read|build|mutation]", "Set or upgrade repository trust"),
            ("/new", "Start a fresh independent session"),
            ("/resume [id]", "Resume a durable repository session"),
            ("/clone", "Clone governed context into an independent session"),
            ("/models", "Select and persist the repository model"),
            ("/auth openai-codex [login|status|logout]", "Manage Codex authentication"),
            ("/reasoning [level]", "Set reasoning effort for the active model (none|minimal|low|medium|high)"),
            ("/thinking", "Show or hide the latest reasoning (Ctrl+T on an empty composer)"),
            ("/extensions", "Browse, load, and unload extensions (Up/Down, Enter)"),
            ("/tools", "Browse and toggle repository tool availability (Up/Down, Enter)"),
            ("/fetch-authorize <url> [redirect ...]", "Authorize one exact URL chain for web_fetch"),
            ("/mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource|prompt|auth|logout|revoke|switch-account|diagnose]", "Manage MCP profiles and capabilities"),
            ("/hooks [list|inspect|enable|disable|test|approve|revoke|audit]", "Govern lifecycle hooks"),
            ("/context [mode|inspect|compact]", "Inspect or control bounded conversation context"),
            ("/validation retry", "Resume interrupted post-apply validation"),
            ("/agents <id> [cancel|cancel-child <id>]", "Inspect or cancel a delegation tree"),
            ("/skills [list|inspect|verify|enable|disable|pin|use|status|cancel]", "Govern skills"),
            ("/plan-policy [name|current]", "Select or report plan approval policy"),
            ("/policy [name|current]", "Select or report mutation approval policy"),
            ("/theme [id|current]", "Select, change, or report the active theme"),
            ("/help", "Show commands"),
            ("/quit", "End the interactive session"),
        ];

        var output = new StringBuilder();
        foreach ((var command, var description) in entries)
        {
            if (command.Length >= descriptionColumn)
            {
                output.AppendLine(command);
                output.Append(' ', descriptionColumn);
            }
            else
            {
                output.Append(command.PadRight(descriptionColumn, ' '));
            }

            output.AppendLine(description);
        }

        output.AppendLine("Submit any other text to Threadsmith.");
        return output.ToString();
    }

    private static string FormatActiveToolLabel(ToolInvocationStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        var detail = string.IsNullOrWhiteSpace(started.ActivityDetail)
            ? string.Empty
            : $" ({started.ActivityDetail})";
        if (started.Source?.Kind == ToolActivitySourceKind.Mcp)
        {
            var identity = string.IsNullOrWhiteSpace(started.Source.DisplayName)
                ? started.ToolName
                : $"{started.Source.DisplayName}/{started.ToolName}";
            return $"MCP: {identity}{detail}";
        }

        return $"TOOLS: {started.ToolName}{detail}";
    }

    private readonly record struct SemanticActivityKey(RunId RunId, SemanticCheckId SemanticCheckId);

    private static string FormatActiveSemanticCheckLabel(SemanticCheckStarted started)
    {
        ArgumentNullException.ThrowIfNull(started);
        return $"SEMANTIC CHECKS: {started.CheckName}";
    }

    private static bool TryGetLatestSemanticActivity(
        IReadOnlyDictionary<SemanticActivityKey, TuiActivity> activitiesByKey,
        IReadOnlyList<SemanticActivityKey> activityOrder,
        out SemanticActivityKey key,
        out TuiActivity? activity)
    {
        for (var index = activityOrder.Count - 1; index >= 0; index--)
        {
            var candidate = activityOrder[index];
            if (activitiesByKey.TryGetValue(candidate, out activity))
            {
                key = candidate;
                return true;
            }
        }

        key = default;
        activity = null;
        return false;
    }

    private static void RemoveSemanticActivitiesForRun(
        IDictionary<SemanticActivityKey, TuiActivity> activitiesByKey,
        IList<SemanticActivityKey> activityOrder,
        RunId runId)
    {
        for (var index = activityOrder.Count - 1; index >= 0; index--)
        {
            var key = activityOrder[index];
            if (key.RunId == runId)
            {
                activityOrder.RemoveAt(index);
                activitiesByKey.Remove(key);
            }
        }
    }

    private static string FormatSessionChoice(SessionCatalogEntry entry, SessionId activeSessionId)
    {
        var current = entry.SessionId == activeSessionId ? "current; " : string.Empty;
        var clone = entry.CloneSourceSessionId is null ? string.Empty : "; clone";
        var preview = string.IsNullOrWhiteSpace(entry.Preview) ? "no visible messages" : entry.Preview;
        var model = entry.ModelSelection is null
            ? "unknown model"
            : $"{entry.ModelSelection.ProviderId}/{entry.ModelSelection.ProfileId.Value:D} ({entry.ModelSelection.ReasoningLevel.ToLowerInvariant()})";
        return $"{entry.SessionId.Value:D} — {current}{entry.State}{clone} — {entry.UpdatedAt:u} — {model} — {preview}";
    }

    private static string FormatToolChoice(ToolStateEntry state)
    {
        var availability = state.Enabled
            ? state.ConsentRequired ? "enabled with user consent" : "enabled"
            : state.ConsentRequired ? "consent required" : "disabled";
        var essential = state.Essential ? " (essential)" : string.Empty;
        return $"[{availability}] {state.DisplayName} ({state.Id}) — {state.Category} — {state.Source}{essential}";
    }

    private async Task HandleMcpCommandAsync(
        TuiController controller,
        string commandText,
        CancellationToken cancellationToken)
    {
        var argumentsText = commandText.Length == 4 ? string.Empty : commandText[4..].Trim();
        var parsedParts = SplitMcpArguments(argumentsText);
        if (parsedParts is null)
        {
            await _surface.WriteAsync(
                "MCP arguments contain an unmatched quote.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var parts = parsedParts;
        var actionText = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant();
        McpManagementAction? action = actionText switch
        {
            "list" => McpManagementAction.List,
            "inspect" => McpManagementAction.Inspect,
            "connect" => McpManagementAction.Connect,
            "disconnect" => McpManagementAction.Disconnect,
            "reconnect" => McpManagementAction.Reconnect,
            "capabilities" => McpManagementAction.ListCapabilities,
            "capability" => McpManagementAction.InspectCapability,
            "enable" => McpManagementAction.EnableTool,
            "disable" => McpManagementAction.DisableTool,
            "resource" when parts.ElementAtOrDefault(1)?.Equals("read", StringComparison.OrdinalIgnoreCase) is true
                => McpManagementAction.ReadResource,
            "prompt" when parts.ElementAtOrDefault(1)?.Equals("get", StringComparison.OrdinalIgnoreCase) is true
                => McpManagementAction.GetPrompt,
            "auth" => McpManagementAction.Authenticate,
            "logout" => McpManagementAction.Logout,
            "revoke" => McpManagementAction.Revoke,
            "switch-account" => McpManagementAction.SwitchAccount,
            "diagnose" => McpManagementAction.Diagnose,
            _ => null,
        };
        if (action is null)
        {
            await _surface.WriteAsync(
                "Usage: /mcp [list|inspect|connect|disconnect|reconnect|capabilities|capability|enable|disable|resource read|prompt get|auth|logout|revoke|switch-account|diagnose] [profile] [capability] [key=value ...]\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var profileIndex = action is McpManagementAction.ReadResource or McpManagementAction.GetPrompt ? 2 : 1;
        var profileId = parts.ElementAtOrDefault(profileIndex);
        if (action != McpManagementAction.List && string.IsNullOrWhiteSpace(profileId))
        {
            profileId = await SelectMcpProfileAsync(controller, cancellationToken);
            if (profileId is null)
            {
                return;
            }
        }

        var requiresCapability = action is McpManagementAction.InspectCapability
            or McpManagementAction.EnableTool
            or McpManagementAction.DisableTool
            or McpManagementAction.ReadResource
            or McpManagementAction.GetPrompt;
        var capabilityIndex = profileIndex + 1;
        McpManagedCapabilityKind? capabilityKind = null;
        if (action == McpManagementAction.ListCapabilities
            && parts.ElementAtOrDefault(capabilityIndex) is { } kindText)
        {
            capabilityKind = ParseMcpCapabilityKind(kindText);
            if (capabilityKind is null)
            {
                await _surface.WriteAsync(
                    $"Unknown MCP capability kind '{kindText}'.\n",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }
        }

        var capabilityId = requiresCapability ? parts.ElementAtOrDefault(capabilityIndex) : null;
        if (requiresCapability && string.IsNullOrWhiteSpace(capabilityId))
        {
            McpManagedCapabilityKind? kind = action switch
            {
                McpManagementAction.EnableTool or McpManagementAction.DisableTool => McpManagedCapabilityKind.Tool,
                McpManagementAction.ReadResource => null,
                McpManagementAction.GetPrompt => McpManagedCapabilityKind.Prompt,
                _ => null,
            };
            var exactProfileId = profileId
                ?? throw new InvalidOperationException("An exact MCP profile is required for capability selection.");
            capabilityId = await SelectMcpCapabilityAsync(
                controller,
                exactProfileId,
                kind,
                action == McpManagementAction.ReadResource,
                cancellationToken);
            if (capabilityId is null)
            {
                return;
            }
        }

        var confirmed = false;
        var allowLocalCleanup = false;
        var revokeBeforeSwitch = false;
        if (action is McpManagementAction.Logout
            or McpManagementAction.Revoke
            or McpManagementAction.SwitchAccount)
        {
            var profileResult = await controller.ManageMcpAsync(
                new McpManagementRequest
                {
                    Action = McpManagementAction.Inspect,
                    ProfileId = profileId,
                },
                cancellationToken);
            if (!profileResult.Succeeded || profileResult.Profile is null)
            {
                await _surface.WriteAsync(
                    FormatMcpResult(profileResult),
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }

            var identity = profileResult.Profile.Summary.EndpointIdentity;
            if (action == McpManagementAction.SwitchAccount)
            {
                var switchMode = await _surface.SelectAsync(
                    $"Replace the MCP identity for '{profileId}' ({identity})",
                    ["Local logout, then authenticate", "Remote revoke, then authenticate", "Cancel"],
                    cancellationToken);
                if (switchMode == 2)
                {
                    return;
                }

                revokeBeforeSwitch = switchMode == 1;
            }
            else
            {
                var confirmation = await _surface.SelectAsync(
                    $"Confirm {action} for MCP profile '{profileId}' ({identity})",
                    ["Confirm exact profile", "Cancel"],
                    cancellationToken);
                if (confirmation != 0)
                {
                    return;
                }
            }

            confirmed = true;
        }

        var argumentStart = action == McpManagementAction.ListCapabilities && capabilityKind is not null
            ? capabilityIndex + 1
            : requiresCapability ? capabilityIndex + 1 : profileIndex + 1;
        if (action is not (McpManagementAction.ReadResource or McpManagementAction.GetPrompt)
            && parts.Length > argumentStart)
        {
            await _surface.WriteAsync(
                "This MCP action does not accept additional arguments.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var arguments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var argument in parts.Skip(argumentStart))
        {
            var pair = argument.Split('=', 2);
            if (pair.Length != 2 || string.IsNullOrWhiteSpace(pair[0]))
            {
                await _surface.WriteAsync(
                    "MCP resource and prompt arguments must use exact key=value syntax.\n",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }

            if (!arguments.TryAdd(pair[0], pair[1]))
            {
                await _surface.WriteAsync(
                    $"MCP argument '{pair[0]}' was supplied more than once.\n",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }
        }

        await _surface.WriteAsync(
            $"MCP: {action.Value} {profileId ?? "all"}\n",
            TuiTextRole.Status,
            cancellationToken);
        var result = await controller.ManageMcpAsync(
            new McpManagementRequest
            {
                Action = action.Value,
                ProfileId = profileId,
                CapabilityId = capabilityId,
                CapabilityKind = capabilityKind,
                Arguments = arguments,
                Confirmed = confirmed,
                AllowLocalCleanupAfterUnconfirmedRevocation = allowLocalCleanup,
                RevokeCurrentIdentityBeforeSwitch = revokeBeforeSwitch,
            },
            cancellationToken);
        if (action is McpManagementAction.Revoke or McpManagementAction.SwitchAccount
            && result.FailureKind == McpManagementFailureKind.RemoteRevocationUnconfirmed)
        {
            var cleanup = await _surface.SelectAsync(
                "Remote revocation is unconfirmed",
                ["Keep local identity and retry later", "Clear local identity only"],
                cancellationToken);
            if (cleanup == 1)
            {
                result = await controller.ManageMcpAsync(
                    new McpManagementRequest
                    {
                        Action = action.Value,
                        ProfileId = profileId,
                        Confirmed = true,
                        AllowLocalCleanupAfterUnconfirmedRevocation = true,
                        RevokeCurrentIdentityBeforeSwitch = action == McpManagementAction.SwitchAccount,
                    },
                    cancellationToken);
            }
        }

        await _surface.WriteAsync(
            FormatMcpResult(result),
            result.Succeeded ? TuiTextRole.Status : TuiTextRole.Error,
            cancellationToken);
    }

    private async Task<string?> SelectMcpProfileAsync(
        TuiController controller,
        CancellationToken cancellationToken)
    {
        var result = await controller.ManageMcpAsync(
            new McpManagementRequest { Action = McpManagementAction.List },
            cancellationToken);
        if (result.Profiles.Count == 0)
        {
            await _surface.WriteAsync("No trusted MCP profiles are configured.\n", TuiTextRole.Status, cancellationToken);
            return null;
        }

        var selected = await _surface.SelectAsync(
            "Select MCP profile",
            [.. result.Profiles.Select(profile =>
                $"{profile.DisplayName} ({profile.ProfileId}) — {profile.State}; {profile.Transport}; {profile.EndpointIdentity}")],
            cancellationToken);
        return result.Profiles[selected].ProfileId;
    }

    private async Task<string?> SelectMcpCapabilityAsync(
        TuiController controller,
        string profileId,
        McpManagedCapabilityKind? kind,
        bool resourcesOnly,
        CancellationToken cancellationToken)
    {
        var result = await controller.ManageMcpAsync(
            new McpManagementRequest
            {
                Action = McpManagementAction.ListCapabilities,
                ProfileId = profileId,
                CapabilityKind = kind,
            },
            cancellationToken);
        McpCapabilityDescriptor[] capabilities =
        [
            .. result.Capabilities.Where(capability => !resourcesOnly
                || capability.Kind is McpManagedCapabilityKind.Resource or McpManagedCapabilityKind.ResourceTemplate),
        ];
        if (capabilities.Length == 0)
        {
            await _surface.WriteAsync("No matching active MCP capabilities. Connect the profile first.\n", TuiTextRole.Status, cancellationToken);
            return null;
        }

        var selected = await _surface.SelectAsync(
            "Select MCP capability",
            [.. capabilities.Select(capability =>
                $"{capability.Name} ({capability.CapabilityId}) — {capability.Kind}"
                + (capability.Enabled is null ? string.Empty : capability.Enabled.Value ? "; enabled" : "; disabled"))],
            cancellationToken);
        return capabilities[selected].CapabilityId;
    }

    private static string[]? SplitMcpArguments(string value)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var quote = '\0';
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else if (character == '\\' && index + 1 < value.Length
                    && value[index + 1] is '\\' or '\'' or '"')
                {
                    current.Append(value[++index]);
                }
                else
                {
                    current.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }

        if (quote != '\0')
        {
            return null;
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return [.. parts];
    }

    private static McpManagedCapabilityKind? ParseMcpCapabilityKind(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "tool" or "tools" => McpManagedCapabilityKind.Tool,
            "resource" or "resources" => McpManagedCapabilityKind.Resource,
            "resource-template" or "resource-templates" => McpManagedCapabilityKind.ResourceTemplate,
            "prompt" or "prompts" => McpManagedCapabilityKind.Prompt,
            _ => null,
        };
    }

    private string FormatMcpResult(McpManagementResult result)
    {
        var output = new StringBuilder();
        output.AppendLine(result.Message);
        if (_displayOptions.ShowOperationDurations && result.DurationMilliseconds is { } duration)
        {
            output.AppendLine($"Operation duration: {duration} ms");
        }

        foreach (var profile in result.Profiles)
        {
            output.AppendLine(
                $"[{profile.State}] {profile.DisplayName} ({profile.ProfileId}) — {profile.Transport}; "
                + $"{profile.EndpointIdentity}; auth {profile.AuthenticationState}; generation {profile.Generation}; "
                + $"capabilities {profile.CapabilityCounts.Values.Sum()}; enabled tools {profile.EnabledToolCount}");
        }

        if (result.Profile is { } detail)
        {
            var profile = detail.Summary;
            output.AppendLine($"Profile: {profile.DisplayName} ({profile.ProfileId})");
            output.AppendLine($"State: {profile.State}; eligible: {profile.Eligible}; source: {profile.ConfigurationSource}");
            output.AppendLine($"Endpoint: {profile.EndpointIdentity}; auth: {profile.AuthenticationState}");
            output.AppendLine(
                $"Timeouts ms: startup {detail.StartupTimeoutMilliseconds}; request {detail.RequestTimeoutMilliseconds}; drain {detail.DrainKillTimeoutMilliseconds}");
            foreach (var latency in detail.Latencies)
            {
                output.AppendLine(
                    $"Latency {latency.Measurement}: count {latency.SampleCount}; min {latency.MinimumMilliseconds?.ToString() ?? "--"}; "
                    + $"max {latency.MaximumMilliseconds?.ToString() ?? "--"}; mean {latency.MeanMilliseconds?.ToString("F1") ?? "--"} ms");
            }
        }

        foreach (var capability in result.Capabilities)
        {
            output.AppendLine(
                $"[{capability.Kind}] {capability.Name} ({capability.CapabilityId}) — digest {capability.Digest}"
                + (capability.Enabled is null ? string.Empty : capability.Enabled.Value ? "; enabled" : "; disabled"));
            if (!string.IsNullOrWhiteSpace(capability.Description))
            {
                output.AppendLine($"  {capability.Description}");
            }

            if (!string.IsNullOrWhiteSpace(capability.ResourceIdentity))
            {
                output.AppendLine($"  Resource: {capability.ResourceIdentity}; MIME: {capability.MimeType ?? "unknown"}");
            }

            foreach (var argument in capability.Arguments)
            {
                output.AppendLine($"  Argument {argument.Name}{(argument.Required ? " (required)" : string.Empty)}: {argument.Description}");
            }

            if (!string.IsNullOrWhiteSpace(capability.InputSchemaJson))
            {
                output.AppendLine($"  Input schema: {capability.InputSchemaJson}");
            }
        }

        foreach (var item in result.Content)
        {
            output.AppendLine($"[UNTRUSTED MCP {item.Label}; {item.MimeType ?? "text"}{(item.IsTruncated ? "; truncated" : string.Empty)}]");
            output.AppendLine(item.Text);
        }

        if (result.IsTruncated && result.Content.All(item => !item.IsTruncated))
        {
            output.AppendLine("[UNTRUSTED MCP content truncated by host bounds]");
        }

        foreach (var check in result.Diagnostics)
        {
            output.AppendLine(
                $"[{(check.Succeeded ? "pass" : "fail")}] {check.Name}: {check.Detail}"
                + (check.DurationMilliseconds is null ? string.Empty : $" ({check.DurationMilliseconds} ms)"));
        }

        return output.ToString();
    }

    private async Task HandleHooksCommandAsync(
        TuiController controller,
        string? repositoryIdentity,
        string commandText,
        CancellationToken cancellationToken)
    {
        var arguments = commandText.Length == 6 ? string.Empty : commandText[6..].Trim();
        var parts = arguments.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var action = parts.Length == 0 ? "list" : parts[0].ToLowerInvariant();
        var id = parts.Length == 2 ? parts[1] : null;
        try
        {
            if (action == "list")
            {
                var handlers = await controller.ListHooksAsync(cancellationToken);
                var output = handlers.Count == 0
                    ? "No lifecycle hooks are configured.\n"
                    : string.Join(
                        Environment.NewLine,
                        handlers.Select(handler =>
                            $"[{(handler.Enabled ? "enabled" : "disabled")}] {handler.Identity.Id.Value} "
                            + $"({handler.Scope}; {handler.AdapterKind}; {string.Join(',', handler.HookPoints)})"))
                        + Environment.NewLine;
                await _surface.WriteAsync(output, TuiTextRole.Status, cancellationToken);
                return;
            }

            if (action == "audit")
            {
                HookHandlerId? handlerId = string.IsNullOrWhiteSpace(id) ? null : new HookHandlerId(id);
                var records = await controller.QueryHookAuditAsync(
                    repositoryIdentity,
                    handlerId,
                    cancellationToken: cancellationToken);
                var output = records.Count == 0
                    ? "No lifecycle-hook audit records matched.\n"
                    : string.Join(
                        Environment.NewLine,
                        records.Select(record =>
                            $"{record.RecordedAt:O} {record.HookPoint} {record.HandlerIdentity.Id.Value}: "
                            + $"{record.Status}/{record.Decision} {record.Code}"))
                        + Environment.NewLine;
                await _surface.WriteAsync(output, TuiTextRole.Status, cancellationToken);
                return;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                await _surface.WriteAsync(
                    "Usage: /hooks [list|inspect <id>|enable <id>|disable <id>|test <id>|approve <id>|revoke <id>|audit [id]]\n",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }

            var selectedId = new HookHandlerId(id);
            var descriptor = await controller.InspectHookAsync(selectedId, cancellationToken);
            if (descriptor is null)
            {
                await _surface.WriteAsync($"Hook '{id}' was not found.\n", TuiTextRole.Error, cancellationToken);
                return;
            }

            switch (action)
            {
                case "inspect":
                    await _surface.WriteAsync(
                        $"Hook: {descriptor.Identity.Id.Value}@{descriptor.Identity.Version}\n"
                        + $"Scope: {descriptor.Scope}\nAdapter: {descriptor.AdapterKind}\nTarget: {descriptor.Target}\n"
                        + $"Enabled: {descriptor.Enabled}\nPoints: {string.Join(", ", descriptor.HookPoints)}\n"
                        + $"Digest: {descriptor.Identity.ConfigurationDigest.Value}\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    break;
                case "enable":
                case "disable":
                    var enabled = action == "enable";
                    var changed = await controller.SetHookEnabledAsync(selectedId, enabled, cancellationToken);
                    await _surface.WriteAsync(
                        changed ? $"Hook '{id}' {(enabled ? "enabled" : "disabled")}.\n" : $"Hook '{id}' was not changed.\n",
                        changed ? TuiTextRole.Status : TuiTextRole.Error,
                        cancellationToken);
                    break;
                case "test":
                    var result = await controller.TestHookAsync(
                        selectedId,
                        repositoryIdentity,
                        cancellationToken);
                    await _surface.WriteAsync(
                        $"Hook '{id}' test: {result.Decision}; {result.AuditRecords.Count} invocation(s).\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    break;
                case "approve":
                    if (descriptor.Scope != HookHandlerScope.Repository || repositoryIdentity is null)
                    {
                        await _surface.WriteAsync(
                            "Approval requires an open repository and a repository-scoped hook.\n",
                            TuiTextRole.Error,
                            cancellationToken);
                        break;
                    }

                    var approved = await controller.ApproveRepositoryHookAsync(
                        new HookRepositoryApproval
                        {
                            RepositoryIdentity = repositoryIdentity,
                            HandlerIdentity = descriptor.Identity,
                            Target = descriptor.Target,
                            HookPoints = descriptor.HookPoints,
                            SecretReferences = descriptor.SecretReferences,
                            ApprovedAt = DateTimeOffset.UtcNow,
                        },
                        cancellationToken);
                    await _surface.WriteAsync(
                        approved ? $"Hook '{id}' approved for this repository.\n" : $"Hook '{id}' approval was rejected.\n",
                        approved ? TuiTextRole.Status : TuiTextRole.Error,
                        cancellationToken);
                    break;
                case "revoke":
                    if (repositoryIdentity is null)
                    {
                        await _surface.WriteAsync("Revocation requires an open repository.\n", TuiTextRole.Error, cancellationToken);
                        break;
                    }

                    _ = await controller.RevokeRepositoryHookAsync(repositoryIdentity, selectedId, cancellationToken);
                    await _surface.WriteAsync($"Hook '{id}' approval revoked.\n", TuiTextRole.Status, cancellationToken);
                    break;
                default:
                    await _surface.WriteAsync("Unknown /hooks action. Enter /help for commands.\n", TuiTextRole.Error, cancellationToken);
                    break;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _surface.WriteAsync(
                $"Lifecycle-hook command failed: {exception.Message}{Environment.NewLine}",
                TuiTextRole.Error,
                cancellationToken);
        }
    }

    private async Task HandleContextCommandAsync(
        TuiController controller,
        string commandText,
        CancellationToken cancellationToken)
    {
        if (controller.SessionId is not { } sessionId)
        {
            await _surface.WriteAsync("No active session.\n", TuiTextRole.Status, cancellationToken);
            return;
        }

        var argument = commandText.Length == 8 ? "mode" : commandText[8..].Trim();
        if (string.Equals(argument, "mode", StringComparison.OrdinalIgnoreCase))
        {
            var state = await _presenter.GetConversationStateAsync(
                sessionId,
                cancellationToken);
            await _surface.WriteAsync(
                $"Conversation context mode: {FormatConversationMode(state.Mode)} (session-state)\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        if (argument.StartsWith("mode ", StringComparison.OrdinalIgnoreCase))
        {
            var value = argument[5..].Trim();
            if (!TryParseConversationMode(value, out var mode))
            {
                await _surface.WriteAsync(
                    "Usage: /context mode [conversation-aware|governed-memory|stateless]\n",
                    TuiTextRole.Warning,
                    cancellationToken);
                return;
            }

            var changed = await _presenter.SetConversationModeAsync(
                sessionId,
                mode,
                cancellationToken);
            await _surface.WriteAsync(
                changed
                    ? $"Conversation context mode set to {FormatConversationMode(mode)} for the next turn.\n"
                    : "Conversation context mode could not be changed.\n",
                changed ? TuiTextRole.Status : TuiTextRole.Warning,
                cancellationToken);
            return;
        }

        if (string.Equals(argument, "inspect", StringComparison.OrdinalIgnoreCase))
        {
            if (controller.LatestRunId is not { } runId)
            {
                await _surface.WriteAsync("No run context is available to inspect.\n", TuiTextRole.Status, cancellationToken);
                return;
            }

            var inspection = await _presenter.GetContextInspectionAsync(
                runId,
                cancellationToken);
            if (inspection is null)
            {
                await _surface.WriteAsync("No context inspection is available for the latest run.\n", TuiTextRole.Status, cancellationToken);
                return;
            }

            var output = $"Context logical {inspection.LogicalTokens}, wire "
                + $"{inspection.WireInputTokens}/{inspection.TokenBudget} tokens; "
                + $"stable prefix {inspection.StablePrefixTokens}; tools {inspection.ToolTransportMode}; "
                + $"mode {FormatConversationMode(inspection.ConversationMode)} ({inspection.ConversationModeSource}); "
                + $"pressure {inspection.ContextPressurePercent:F1}%\n"
                + string.Join(
                    string.Empty,
                    inspection.ConversationItems.Select(item =>
                        $"  {(item.Included ? "included" : "omitted")} {item.Kind} {item.Id}: {item.Rationale}\n"))
                + string.Join(
                    string.Empty,
                    inspection.Reductions.Select(reduction => $"  reduced: {reduction}\n"));
            await _surface.WriteAsync(output, TuiTextRole.Status, cancellationToken);
            return;
        }

        if (string.Equals(argument, "compact", StringComparison.OrdinalIgnoreCase))
        {
            var compacted = await _presenter.CompactConversationAsync(sessionId, cancellationToken);
            await _surface.WriteAsync(
                compacted
                    ? "Conversation compaction completed or was already current.\n"
                    : "Conversation compaction failed; the prior snapshot remains active.\n",
                compacted ? TuiTextRole.Status : TuiTextRole.Warning,
                cancellationToken);
            return;
        }

        await _surface.WriteAsync(
            "Usage: /context [mode [conversation-aware|governed-memory|stateless]|inspect|compact]\n",
            TuiTextRole.Warning,
            cancellationToken);
    }

    private async Task HandleSkillsCommandAsync(
        TuiController controller,
        SessionId sessionId,
        RepositoryTrustLevel trust,
        string commandText,
        CancellationToken cancellationToken)
    {
        var arguments = commandText.Length == 7 ? string.Empty : commandText[7..].Trim();
        string operation;
        string remainder;
        var separator = arguments.IndexOf(' ');
        if (separator < 0)
        {
            operation = string.IsNullOrWhiteSpace(arguments) ? "list" : arguments;
            remainder = string.Empty;
        }
        else
        {
            operation = arguments[..separator];
            remainder = arguments[(separator + 1)..].Trim();
        }

        try
        {
            switch (operation.ToLowerInvariant())
            {
                case "list":
                    var candidates = await controller.ListSkillsAsync(
                        new SkillCatalogQuery
                        {
                            Text = string.IsNullOrWhiteSpace(remainder) ? null : remainder,
                        },
                        cancellationToken);
                    var listing = candidates.Count == 0
                        ? "No skills matched.\n"
                        : string.Join(string.Empty, candidates.Select(candidate =>
                        {
                            var claude = candidate.Provenance.Source.StartsWith(
                                "claude:",
                                StringComparison.Ordinal);
                            var selector = claude
                                ? $"claude:{candidate.Provenance.Scope}:{candidate.Metadata.DisplayName}"
                                : $"native:{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
                                    + $"@{candidate.Metadata.Version}";
                            return $"{selector} [{candidate.Verification}] "
                                + $"{(candidate.Enabled ? "enabled" : "disabled")}\n";
                        }));
                    await _surface.WriteAsync(listing, TuiTextRole.Status, cancellationToken);
                    return;

                case "refresh":
                    var refreshed = await controller.RefreshSkillsAsync(cancellationToken);
                    var claudeCount = refreshed.Candidates.Count(candidate =>
                        candidate.Provenance.Source.StartsWith("claude:", StringComparison.Ordinal));
                    await _surface.WriteAsync(
                        $"Skill catalog generation {refreshed.Generation}: "
                            + $"{refreshed.Candidates.Count - claudeCount} native and "
                            + $"{claudeCount} Claude-style candidate(s).\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "inspect":
                case "provenance":
                    var inspectSelector = RequireSkillArgument(remainder);
                    if (inspectSelector.StartsWith("claude:", StringComparison.OrdinalIgnoreCase))
                    {
                        var claude = ResolveClaudeSkill(inspectSelector);
                        await _surface.WriteAsync(
                            FormatClaudeSkillCandidate(claude),
                            TuiTextRole.Status,
                            cancellationToken);
                        return;
                    }

                    var inspected = await controller.GetSkillAsync(
                        inspectSelector,
                        cancellationToken);
                    var compatibility = await controller.GetSkillCompatibilityAsync(
                        inspectSelector,
                        CreateSkillCompatibilityRequest(sessionId, trust, inspectSelector),
                        cancellationToken);
                    await _surface.WriteAsync(
                        FormatSkillCandidate(inspected, compatibility),
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "install":
                    (var archivePath, var source) = ParseSkillInstall(remainder);
                    var installed = await controller.InstallSkillAsync(
                        archivePath,
                        source,
                        cancellationToken);
                    await _surface.WriteAsync(
                        FormatSkillCandidate(installed),
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "uninstall":
                    var removed = await controller.UninstallSkillAsync(
                        RequireSkillArgument(remainder),
                        cancellationToken);
                    await _surface.WriteAsync(
                        removed ? "Skill package uninstalled.\n" : "Skill package was not installed.\n",
                        removed ? TuiTextRole.Status : TuiTextRole.Warning,
                        cancellationToken);
                    return;

                case "verify":
                    var verified = await controller.VerifySkillAsync(
                        RequireSkillArgument(remainder),
                        cancellationToken);
                    var verificationRole = verified.Verification == SkillVerificationState.Invalid
                        ? TuiTextRole.Warning
                        : TuiTextRole.Status;
                    await _surface.WriteAsync(
                        FormatSkillCandidate(verified),
                        verificationRole,
                        cancellationToken);
                    return;

                case "enable":
                case "disable":
                    var changed = await controller.SetSkillEnabledAsync(
                        RequireSkillArgument(remainder),
                        operation.Equals("enable", StringComparison.OrdinalIgnoreCase),
                        cancellationToken);
                    await _surface.WriteAsync(
                        FormatSkillCandidate(changed),
                        changed.Enabled ? TuiTextRole.Status : TuiTextRole.Warning,
                        cancellationToken);
                    return;

                case "pin":
                    var pinned = await controller.PinSkillAsync(
                        RequireSkillArgument(remainder),
                        cancellationToken);
                    await _surface.WriteAsync(
                        $"Pinned {pinned.SkillId.Value}@{pinned.Version} "
                            + $"sha256:{pinned.Digest.Value}.\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "use":
                    (var selector, var input) = ParseSkillUse(remainder);
                    var invoked = await controller.InvokeSkillAsync(
                        new SkillInvocationRequest
                        {
                            InvocationId = SkillInvocationId.New(),
                            SessionId = sessionId,
                            RunId = RunId.New(),
                            Selector = selector,
                            InputJson = input,
                            Trust = trust,
                            Phase = RunPhase.EvidenceCollection,
                            HostBudget = new SkillBudget(),
                        },
                        cancellationToken);
                    var invocationRole = invoked.Status == SkillInvocationStatus.Failed
                        ? TuiTextRole.Warning
                        : TuiTextRole.Status;
                    await _surface.WriteAsync(
                        FormatSkillInvocation(invoked),
                        invocationRole,
                        cancellationToken);
                    return;

                case "continue":
                    (var continueId, var hostResult) = ParseSkillContinuation(remainder);
                    var continued = await controller.ContinueSkillAsync(
                        continueId,
                        hostResult,
                        cancellationToken);
                    await _surface.WriteAsync(
                        FormatSkillInvocation(continued),
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "resume":
                    if (!Guid.TryParse(remainder, out var resumeId))
                    {
                        throw new ArgumentException("Skill resume requires an invocation GUID.");
                    }

                    var resumed = await controller.ResumeSkillAsync(
                        new SkillInvocationId(resumeId),
                        cancellationToken);
                    await _surface.WriteAsync(
                        FormatSkillInvocation(resumed),
                        TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "status":
                    if (!Guid.TryParse(remainder, out var statusId))
                    {
                        throw new ArgumentException("Skill status requires an invocation GUID.");
                    }

                    var checkpoint = await controller.GetSkillInvocationAsync(
                        new SkillInvocationId(statusId),
                        cancellationToken);
                    await _surface.WriteAsync(
                        checkpoint is null
                            ? "Skill invocation was not found.\n"
                            : $"Skill {checkpoint.InvocationId.Value:D}: {checkpoint.Status}; "
                                + $"{checkpoint.Package.SkillId.Value}@{checkpoint.Package.Version}; "
                                + $"next: {checkpoint.NextAction}\n",
                        checkpoint is null ? TuiTextRole.Warning : TuiTextRole.Status,
                        cancellationToken);
                    return;

                case "cancel":
                    if (!Guid.TryParse(remainder, out var cancelId))
                    {
                        throw new ArgumentException("Skill cancellation requires an invocation GUID.");
                    }

                    var cancelled = await controller.CancelSkillInvocationAsync(
                        new SkillInvocationId(cancelId),
                        cancellationToken);
                    await _surface.WriteAsync(
                        cancelled ? "Skill cancellation requested.\n" : "Skill invocation is not active.\n",
                        cancelled ? TuiTextRole.Status : TuiTextRole.Warning,
                        cancellationToken);
                    return;

                default:
                    await _surface.WriteAsync(
                        "Usage: /skills [list [text]|refresh|inspect <selector>|provenance <selector>|"
                            + "install <archive-path> <source>|uninstall <selector>|verify <selector>|"
                            + "enable <selector>|disable <selector>|pin <selector>|use <selector> <json>|continue <invocation-id> <json>|resume <invocation-id>|"
                            + "status <invocation-id>|cancel <invocation-id>]\n",
                        TuiTextRole.Warning,
                        cancellationToken);
                    return;
            }
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidDataException
            or InvalidOperationException
            or KeyNotFoundException
            or UnauthorizedAccessException
            or NotSupportedException)
        {
            await _surface.WriteAsync(
                $"Skill command failed: {exception.Message}\n",
                TuiTextRole.Warning,
                cancellationToken);
        }
    }

    private ClaudeSkillCandidate ResolveClaudeSkill(string selector)
    {
        var parts = selector.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !Enum.TryParse(parts[1], true, out SkillScope scope)
            || _claudeSkills is null)
        {
            throw new ArgumentException(
                "Claude skill selectors use claude:<scope>:<name>.",
                nameof(selector));
        }

        return _claudeSkills.Candidates.SingleOrDefault(candidate =>
                candidate.Identity.Scope == scope
                && string.Equals(candidate.Identity.Name, parts[2], StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"Claude-style skill '{selector}' was not found.");
    }

    private static string FormatClaudeSkillCandidate(ClaudeSkillCandidate candidate)
    {
        var mapped = candidate.MappedTools.Count == 0
            ? "none"
            : string.Join(", ", candidate.MappedTools);
        var unavailable = candidate.UnavailableTools.Count == 0
            ? "none"
            : string.Join(", ", candidate.UnavailableTools);
        var reasons = candidate.ReasonCodes.Count == 0
            ? "none"
            : string.Join(", ", candidate.ReasonCodes);
        return $"Claude-style skill: {candidate.Identity.Scope}:{candidate.Identity.Name}\n"
            + $"  contract: {candidate.Version}\n"
            + $"  compatibility: {candidate.Status}\n"
            + $"  description: {candidate.Description}\n"
            + $"  mapped tools: {mapped}\n"
            + $"  unavailable tools: {unavailable}\n"
            + $"  restrictions: {reasons}\n"
            + "  trust: unsigned compatibility source; exact external enablement required\n";
    }

    private static string RequireSkillArgument(string argument)
    {
        return string.IsNullOrWhiteSpace(argument)
            ? throw new ArgumentException("The skill command requires an explicit selector.")
            : argument;
    }

    private static (string ArchivePath, string Source) ParseSkillInstall(string arguments)
    {
        var separator = arguments.LastIndexOf(' ');
        if (separator < 1 || separator == arguments.Length - 1)
        {
            throw new ArgumentException(
                "Skill install requires an archive path followed by a provenance source.");
        }

        return (arguments[..separator].Trim().Trim('"'), arguments[(separator + 1)..].Trim());
    }

    private static (string Selector, string InputJson) ParseSkillUse(string arguments)
    {
        var separator = arguments.IndexOf(' ');
        if (separator < 1 || separator == arguments.Length - 1)
        {
            throw new ArgumentException("Skill use requires an explicit selector and JSON input.");
        }

        return (arguments[..separator], arguments[(separator + 1)..].Trim());
    }

    private static (SkillInvocationId InvocationId, string HostResultJson) ParseSkillContinuation(
        string arguments)
    {
        var separator = arguments.IndexOf(' ');
        if (separator < 1
            || separator == arguments.Length - 1
            || !Guid.TryParse(arguments[..separator], out var invocationId))
        {
            throw new ArgumentException(
                "Skill continuation requires an invocation GUID and host-result JSON.");
        }

        return (new SkillInvocationId(invocationId), arguments[(separator + 1)..].Trim());
    }

    private static SkillInvocationRequest CreateSkillCompatibilityRequest(
        SessionId sessionId,
        RepositoryTrustLevel trust,
        string selector)
    {
        return new SkillInvocationRequest
        {
            InvocationId = SkillInvocationId.New(),
            SessionId = sessionId,
            RunId = RunId.New(),
            Selector = selector,
            InputJson = "{}",
            Trust = trust,
            Phase = RunPhase.EvidenceCollection,
            HostBudget = new SkillBudget(),
        };
    }

    private static string FormatSkillCandidate(
        SkillCatalogCandidate candidate,
        SkillCompatibilityResult? compatibility = null)
    {
        var requirements = $"  tools={string.Join(',', candidate.Metadata.Requirements.RequiredTools)}; "
            + $"trust={candidate.Metadata.Requirements.MinimumTrust}; "
            + $"models={string.Join(',', candidate.Metadata.Requirements.Model.Workloads)}\n";
        var compatibilityText = compatibility is null
            ? string.Empty
            : $"  compatible={compatibility.IsCompatible}; denials="
                + $"{string.Join(',', compatibility.DenialReasons)}\n";
        return $"{candidate.Provenance.Scope}:{candidate.Metadata.SkillId.Value}"
            + $"@{candidate.Metadata.Version}\n"
            + $"  {candidate.Metadata.DisplayName}: {candidate.Metadata.Description}\n"
            + $"  publisher={candidate.Metadata.Publisher}; source={candidate.Provenance.Source}\n"
            + $"  sha256={candidate.Identity.Digest.Value}; verification={candidate.Verification}; "
            + $"enabled={candidate.Enabled}\n"
            + $"  reason={candidate.VerificationReason}\n"
            + requirements
            + compatibilityText;
    }

    private static string FormatSkillInvocation(SkillInvocationResult result)
    {
        var actions = result.HostActions.Count == 0
            ? string.Empty
            : "\n" + string.Join(
                "\n",
                result.HostActions.Select(item =>
                    $"  proposed {item.Kind} ({item.StepId}): {item.PayloadJson}"));
        return $"Skill invocation {result.InvocationId.Value:D}: {result.Status}; "
            + $"{result.Package.SkillId.Value}@{result.Package.Version}; "
            + $"{result.Reason}; next={result.Checkpoint.NextAction}{actions}\n";
    }

    private async Task HandleAgentsCommandAsync(
        TuiController controller,
        string commandText,
        CancellationToken cancellationToken)
    {
        var arguments = commandText[7..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (arguments.Length == 0 || !Guid.TryParse(arguments[0], out var delegationValue))
        {
            await _surface.WriteAsync(
                "Usage: /agents <delegation-id> [cancel|cancel-child <assignment-id>]\n",
                TuiTextRole.Warning,
                cancellationToken);
            return;
        }

        var delegationId = new DelegationId(delegationValue);
        if (arguments.Length >= 2 && string.Equals(arguments[1], "cancel", StringComparison.OrdinalIgnoreCase))
        {
            var cancelled = await controller.CancelDelegationAsync(delegationId, cancellationToken);
            await _surface.WriteAsync(
                cancelled ? "Delegation cancellation requested.\n" : "Delegation is not active.\n",
                cancelled ? TuiTextRole.Status : TuiTextRole.Warning,
                cancellationToken);
            return;
        }

        if (arguments.Length == 3
            && string.Equals(arguments[1], "cancel-child", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(arguments[2], out var assignmentValue))
        {
            var cancelled = await controller.CancelAgentAssignmentAsync(
                delegationId,
                new AgentAssignmentId(assignmentValue),
                cancellationToken);
            await _surface.WriteAsync(
                cancelled ? "Child cancellation requested.\n" : "Child is not active.\n",
                cancelled ? TuiTextRole.Status : TuiTextRole.Warning,
                cancellationToken);
            return;
        }

        var checkpoint = await controller.GetDelegationAsync(
            delegationId,
            cancellationToken);
        if (checkpoint is null)
        {
            await _surface.WriteAsync("Delegation was not found.\n", TuiTextRole.Warning, cancellationToken);
            return;
        }

        var output = $"Delegation {checkpoint.DelegationId.Value:D}: {checkpoint.Phase}; "
            + $"generation {checkpoint.Provenance.Generation}; next: {checkpoint.NextAction}\n"
            + string.Join(
                string.Empty,
                checkpoint.ChildOutcomes.Select(outcome =>
                    $"  {outcome.AssignmentId.Value:D} {outcome.Status}; "
                    + $"tools {outcome.Usage.ToolCalls}; tokens {outcome.Usage.ModelTokens}; {outcome.Reason}\n"));
        await _surface.WriteAsync(output, TuiTextRole.Status, cancellationToken);
    }

    private static string FormatConversationMode(ConversationContextMode mode)
    {
        return mode switch
        {
            ConversationContextMode.ConversationAware => "conversation-aware",
            ConversationContextMode.GovernedMemoryOnly => "governed-memory",
            ConversationContextMode.Stateless => "stateless",
            _ => mode.ToString(),
        };
    }

    private static bool TryParseConversationMode(
        string value,
        out ConversationContextMode mode)
    {
        if (string.Equals(value, "conversation-aware", StringComparison.OrdinalIgnoreCase))
        {
            mode = ConversationContextMode.ConversationAware;
            return true;
        }

        if (string.Equals(value, "governed-memory", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "governed-memory-only", StringComparison.OrdinalIgnoreCase))
        {
            mode = ConversationContextMode.GovernedMemoryOnly;
            return true;
        }

        if (string.Equals(value, "stateless", StringComparison.OrdinalIgnoreCase))
        {
            mode = ConversationContextMode.Stateless;
            return true;
        }

        mode = default;
        return false;
    }

    private async Task HandlePolicyCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        if (_mutationApprovalPolicy is null)
        {
            await _surface.WriteAsync(
                "Mutation policy management is not available in this session.\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        var argument = commandText.Length == 7 ? string.Empty : commandText[7..].Trim();
        if (string.Equals(argument, "current", StringComparison.OrdinalIgnoreCase))
        {
            await _surface.WriteAsync(
                $"Current mutation policy: {_mutationApprovalPolicy.CurrentPolicy}.{Environment.NewLine}",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        MutationApprovalPolicy? selectedPolicy = null;
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (Enum.TryParse(argument, ignoreCase: true, out MutationApprovalPolicy parsed)
                && Enum.IsDefined(parsed))
            {
                selectedPolicy = parsed;
            }
            else
            {
                await _surface.WriteAsync(
                    $"Unknown mutation policy '{argument}'. Enter /policy to list policies.{Environment.NewLine}",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }
        }
        else
        {
            var policies = Enum.GetValues<MutationApprovalPolicy>();
            string[] choices =
            [
                .. policies.Select(policy => FormatPolicyChoice(
                    policy,
                    policy == _mutationApprovalPolicy.CurrentPolicy)),
                "Cancel",
            ];
            var selected = await _surface.SelectAsync(
                $"Mutation policy (current: {_mutationApprovalPolicy.CurrentPolicy}; Up/Down, Enter):",
                choices,
                cancellationToken);
            if (selected < 0 || selected >= policies.Length)
            {
                await _surface.WriteAsync(
                    "Mutation policy unchanged.\n",
                    TuiTextRole.Status,
                    cancellationToken);
                return;
            }

            selectedPolicy = policies[selected];
        }

        var policy = selectedPolicy.Value;
        await _mutationApprovalPolicy.SetPolicyAsync(policy, cancellationToken);
        var warning = policy is MutationApprovalPolicy.TrustPlan
            or MutationApprovalPolicy.TrustSession
            or MutationApprovalPolicy.AlwaysTrustRepo
            ? " Warning: eligible diffs will apply without a separate mutation prompt; hard guardrails and validation remain active."
            : string.Empty;
        var persistence = policy == MutationApprovalPolicy.AlwaysTrustRepo
            ? " This repository-wide choice persists across restarts."
            : string.Empty;
        await _surface.WriteAsync(
            $"Mutation policy changed to {policy}.{warning}{persistence}{Environment.NewLine}",
            TuiTextRole.Status,
            cancellationToken);
    }

    private static string FormatPolicyChoice(MutationApprovalPolicy policy, bool isCurrent)
    {
        var description = policy switch
        {
            MutationApprovalPolicy.ReviewAll => "review every staged diff",
            MutationApprovalPolicy.ReviewRisky => "auto-apply ordinary edits; review risky changes",
            MutationApprovalPolicy.TrustPlan => "auto-apply changes within the approved plan",
            MutationApprovalPolicy.TrustSession => "auto-apply in-repository changes this session",
            MutationApprovalPolicy.AlwaysTrustRepo => "persistently auto-apply in-repository changes",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
        return $"{policy} — {description}{(isCurrent ? " [current]" : string.Empty)}";
    }

    private async Task HandlePlanPolicyCommandAsync(
        TuiController controller,
        string commandText,
        CancellationToken cancellationToken)
    {
        if (_planApprovalPolicy is null)
        {
            await _surface.WriteAsync(
                "Plan policy management is not available in this session.\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        var argument = commandText.Length == 12 ? string.Empty : commandText[12..].Trim();
        if (string.Equals(argument, "current", StringComparison.OrdinalIgnoreCase))
        {
            var currentPolicy = await controller.GetPlanApprovalPolicyAsync(cancellationToken);
            await _surface.WriteAsync(
                $"Current plan policy: {currentPolicy}.{Environment.NewLine}",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        if (string.Equals(argument, "reset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(argument, "revoke", StringComparison.OrdinalIgnoreCase))
        {
            await controller.SetPlanApprovalPolicyAsync(PlanApprovalPolicy.ReviewAll, cancellationToken);
            await _surface.WriteAsync(
                "Plan policy reset to ReviewAll and persisted for this repository; repository plan trust was revoked when present.\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        PlanApprovalPolicy? selectedPolicy = null;
        if (!string.IsNullOrWhiteSpace(argument))
        {
            if (Enum.TryParse(argument, ignoreCase: true, out PlanApprovalPolicy parsed)
                && Enum.IsDefined(parsed))
            {
                selectedPolicy = parsed;
            }
            else
            {
                await _surface.WriteAsync(
                    $"Unknown plan policy '{argument}'. Enter /plan-policy to list policies.{Environment.NewLine}",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }
        }
        else
        {
            var currentPolicy = await controller.GetPlanApprovalPolicyAsync(cancellationToken);
            var policies = Enum.GetValues<PlanApprovalPolicy>();
            string[] choices =
            [
                .. policies.Select(policy => FormatPlanPolicyChoice(
                    policy,
                    policy == currentPolicy)),
                "Cancel",
            ];
            var selected = await _surface.SelectAsync(
                $"Plan policy (current: {currentPolicy}; Up/Down, Enter):",
                choices,
                cancellationToken);
            if (selected < 0 || selected >= policies.Length)
            {
                await _surface.WriteAsync("Plan policy unchanged.\n", TuiTextRole.Status, cancellationToken);
                return;
            }

            selectedPolicy = policies[selected];
        }

        var policy = selectedPolicy.Value;
        await controller.SetPlanApprovalPolicyAsync(policy, cancellationToken);
        var warning = policy is PlanApprovalPolicy.TrustSession
            or PlanApprovalPolicy.AlwaysTrustRepo
            or PlanApprovalPolicy.AutoApproveAllValid
            ? " Warning: valid plans may skip manual plan review; exact-diff mutation approval, pre-mutation screening, and validation remain active."
            : string.Empty;
        var persistence = policy switch
        {
            PlanApprovalPolicy.TrustSession => " This choice is session-only.",
            PlanApprovalPolicy.AlwaysTrustRepo => " This exact repository choice persists with an identity fence.",
            _ => " This repository default persists across restarts.",
        };
        await _surface.WriteAsync(
            $"Plan policy changed to {policy}.{warning}{persistence}{Environment.NewLine}",
            TuiTextRole.Status,
            cancellationToken);
    }

    private static string FormatPlanPolicyChoice(PlanApprovalPolicy policy, bool isCurrent)
    {
        var description = policy switch
        {
            PlanApprovalPolicy.ReviewAll => "review every valid plan",
            PlanApprovalPolicy.ReviewRisky => "auto-approve low-risk valid plans",
            PlanApprovalPolicy.TrustSession => "auto-approve low/moderate valid plans this session",
            PlanApprovalPolicy.AlwaysTrustRepo => "persistently auto-approve low/moderate valid plans for this repository",
            PlanApprovalPolicy.AutoApproveAllValid => "auto-approve every valid non-blocked plan",
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
        return $"{policy} — {description}{(isCurrent ? " [current]" : string.Empty)}";
    }

    private async Task HandleThemeCommandAsync(string commandText, CancellationToken cancellationToken)
    {
        var argument = commandText.Length == 6 ? string.Empty : commandText[6..].Trim();
        if (string.Equals(argument, "current", StringComparison.OrdinalIgnoreCase))
        {
            var current = _themePreferences.ActiveTheme;
            await _surface.WriteAsync(
                $"Current theme: {current.Theme.Id} ({current.Name}).{Environment.NewLine}",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        var selectedId = argument;
        if (string.IsNullOrWhiteSpace(selectedId))
        {
            var active = _themePreferences.ActiveTheme;
            var themes = _themePreferences.Catalog.Themes;
            string[] choices =
            [
                .. themes.Select(theme => $"{theme.Name} ({theme.Theme.Id}){(ReferenceEquals(theme, active) ? " [active]" : string.Empty)}"),
                "Cancel",
            ];
            var selected = await _surface.SelectAsync(
                "Theme (Up/Down, Enter):",
                choices,
                cancellationToken);
            if (selected < 0 || selected >= themes.Count)
            {
                await _surface.WriteAsync("Theme unchanged.\n", TuiTextRole.Status, cancellationToken);
                return;
            }

            selectedId = themes[selected].Theme.Id;
        }

        var safeId = selectedId.Length <= 40
            && selectedId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
        if (!safeId
            || !_themePreferences.Catalog.TryGet(selectedId, out var selectedTheme)
            || selectedTheme is null)
        {
            var displayId = safeId ? $": {selectedId}" : " id";
            await _surface.WriteAsync(
                $"Unknown theme{displayId}. Enter /theme to list themes.{Environment.NewLine}",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        if (_themePreferenceStore is not null)
        {
            try
            {
                await _themePreferenceStore.SetDefaultThemeAsync(selectedTheme.Theme.Id, cancellationToken);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                await _surface.WriteAsync(
                    $"Theme could not be saved; selection unchanged.{Environment.NewLine}",
                    TuiTextRole.Error,
                    cancellationToken);
                return;
            }
        }

        _ = _themePreferences.TrySelect(selectedTheme.Theme.Id, out _);
        await _surface.SetThemeAsync(selectedTheme, cancellationToken);
        var persistence = _themePreferenceStore is null ? string.Empty : " and saved as the default";
        await _surface.WriteAsync(
            $"Theme changed to {selectedTheme.Name} ({selectedTheme.Theme.Id}){persistence}.{Environment.NewLine}",
            TuiTextRole.Status,
            cancellationToken);
    }

    private async Task<RepositoryTrustLevel?> ReadTrustAsync(CancellationToken cancellationToken)
    {
        string[] choices =
        [
            "Trusted Read (safe default: read and index only)",
            "Trusted Build (repository code may execute)",
            "Trusted Mutation (explicitly approved file changes)",
            "Inspect Only",
            "Cancel",
        ];
        var selected = await _surface.SelectAsync(
            "Repository trust (Up/Down, Enter):",
            choices,
            cancellationToken);
        return selected switch
        {
            0 => RepositoryTrustLevel.TrustedRead,
            1 => RepositoryTrustLevel.TrustedBuild,
            2 => RepositoryTrustLevel.TrustedMutation,
            3 => RepositoryTrustLevel.UntrustedInspection,
            _ => null,
        };
    }

    private async Task SetRepositoryPromptAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var repositoryName = TuiSessionStatusFactory.GetRepositoryDisplayName(repositoryPath);
        repositoryName = new string([.. repositoryName
            .Where(character => !char.IsControl(character))
            .Take(40)]);
        await _surface.SetPromptAsync(
            $"{(string.IsNullOrWhiteSpace(repositoryName) ? "threadsmith" : repositoryName)} > ",
            cancellationToken);
    }

    private async Task<RepositoryTrustLevel?> ReadTrustUpgradeAsync(
        RepositoryTrustState persistedTrust,
        CancellationToken cancellationToken)
    {
        string[] choices =
        [
            "Keep Trusted Read",
            "Upgrade to Trusted Build (repository code may execute)",
            "Cancel",
        ];
        var selected = await _surface.SelectAsync(
            $"Repository trust is {persistedTrust.Level} (Up/Down, Enter):",
            choices,
            cancellationToken);
        return selected switch
        {
            0 => RepositoryTrustLevel.TrustedRead,
            1 => RepositoryTrustLevel.TrustedBuild,
            _ => null,
        };
    }

    private async Task<string?> ReadSolutionAsync(
        string repositoryPath,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken)
    {
        string[] choices =
        [
            .. candidates
                .Select(candidate => Path.GetRelativePath(repositoryPath, candidate)),
            "Cancel",
        ];
        var selected = await _surface.SelectAsync(
            "Select a solution (Up/Down, Enter):",
            choices,
            cancellationToken);
        return selected >= 0 && selected < candidates.Count
            ? candidates[selected]
            : null;
    }

    /// <summary>Drives the navigable Extension Manager: lists every discovered extension with its
    /// loaded/unloaded state and lets the user load or unload one (plan-16 task 10).</summary>
    /// <param name="controller">The active TUI controller supplying the session id.</param>
    /// <param name="cancellationToken">A token that cancels the interaction.</param>
    private async Task ManageExtensionsAsync(TuiController controller, CancellationToken cancellationToken)
    {
        if (_extensionManager is null)
        {
            await _surface.WriteAsync(
                "Extensions are not available in this session.\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        var summaries = await _extensionManager.DiscoverAsync(cancellationToken);
        if (summaries.Count == 0)
        {
            await _surface.WriteAsync(
                "No extensions discovered. Place extension packages in the configured discovery directory (.threadsmith/extensions by default).\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            // Refresh state each iteration so the list reflects the latest load/unload outcome.
            summaries = await _extensionManager.DiscoverAsync(cancellationToken);
            string[] choices =
            [
                .. summaries.Select(FormatExtensionChoice),
                "Back",
            ];
            var selected = await _surface.SelectAsync(
                "Extensions (Up/Down, Enter; Back to return):",
                choices,
                cancellationToken);
            if (selected < 0 || selected >= summaries.Count)
            {
                return;
            }

            var summary = summaries[selected];
            await ChooseExtensionActionAsync(controller, summary, cancellationToken);
        }
    }

    private async Task ChooseExtensionActionAsync(
        TuiController controller,
        ExtensionSummary summary,
        CancellationToken cancellationToken)
    {
        string[] actions = summary.IsLoaded
            ? [$"Unload {summary.Name}", "Cancel"]
            : [$"Load {summary.Name}", "Cancel"];
        var action = await _surface.SelectAsync(
            $"{summary.Name} ({summary.Version}) — {summary.State} — {summary.ToolCount} tool(s)",
            actions,
            cancellationToken);
        if (action != 0)
        {
            return;
        }

        var sessionId = controller.SessionId ?? SessionId.New();
        try
        {
            if (summary.IsLoaded)
            {
                var unloaded = await _extensionManager!.UnloadAsync(summary.ExtensionId, sessionId, cancellationToken);
                await _surface.WriteAsync(
                    unloaded
                        ? $"{summary.Name} unloaded.\n"
                        : $"{summary.Name} did not unload cleanly (blocked or not loaded). See diagnostics above.\n",
                    TuiTextRole.Status,
                    cancellationToken);
            }
            else
            {
                var loaded = await _extensionManager!.LoadAsync(summary.ExtensionId, sessionId, cancellationToken);
                await _surface.WriteAsync(
                    loaded is not null
                        ? $"{summary.Name} loaded ({loaded!.State}, {loaded.ToolCount} tool(s)).\n"
                        : $"Extension '{summary.ExtensionId}' was not found in the discovery directory.\n",
                    TuiTextRole.Status,
                    cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _surface.WriteAsync(
                $"Extension operation failed: {exception.Message}\n",
                TuiTextRole.Error,
                cancellationToken);
        }
    }

    private static string FormatExtensionChoice(ExtensionSummary summary)
    {
        var marker = summary.IsLoaded ? "[loaded]  " : "[unloaded]";
        var tools = summary.ToolCount > 0 ? $" — {summary.ToolCount} tool(s)" : string.Empty;
        return $"{marker} {summary.Name} ({summary.Version}) — {summary.State}{tools}";
    }

    private async Task<RepositoryOpenWorkflowResult> OpenRepositoryAsync(
        TuiController controller,
        string repositoryPath,
        RepositoryTrustLevel? requestedTrust,
        string? requestedSolutionPath,
        bool? configurationDirectoryExistedBeforeRuntimeStorage,
        CancellationToken cancellationToken)
    {
        try
        {
            var initialization =
                await controller.GetRepositoryInitializationStatusAsync(
                    repositoryPath,
                    cancellationToken);
            if (configurationDirectoryExistedBeforeRuntimeStorage is false)
            {
                initialization = initialization with { HasConfigurationDirectory = false };
            }

            if (initialization.ShouldOfferInitialization)
            {
                var initialize = await _surface.SelectAsync(
                    "No .threadsmith configuration or .NET solutions found. Initialize a new Threadsmith repository?",
                    ["Initialize", "Continue without configuration"],
                    cancellationToken);
                if (initialize == 0)
                {
                    var initialized =
                        await controller.InitializeRepositoryAsync(repositoryPath, cancellationToken);
                    await _surface.WriteAsync(
                        initialized.Created
                            ? $"Initialized Threadsmith repository: {initialized.ConfigurationPath}{Environment.NewLine}"
                            : $"Threadsmith repository was already initialized: {initialized.ConfigurationPath}{Environment.NewLine}",
                        TuiTextRole.Status,
                        cancellationToken);
                }
            }

            var result = await controller.OpenRepositoryWorkflowAsync(
                repositoryPath,
                ReadTrustAsync,
                (candidates, token) => ReadSolutionAsync(repositoryPath, candidates, token),
                (persistedTrust, token) => ReadTrustUpgradeAsync(persistedTrust, token),
                requestedTrust,
                requestedSolutionPath,
                cancellationToken);
            if (result.UsedRememberedSolution
                && result.Repository is not null
                && result.Solution is not null)
            {
                var relativeSolution = Path.GetRelativePath(
                        result.Repository.RepositoryPath,
                        result.Solution.SolutionPath)
                    .Replace('\\', '/');
                await _surface.WriteAsync(
                    $"Loading remembered solution: {relativeSolution}{Environment.NewLine}" +
                    $"  (Use --solution to change){Environment.NewLine}",
                    TuiTextRole.Status,
                    cancellationToken);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await _surface.WriteAsync(
                FormatStatusError(exception) + Environment.NewLine,
                TuiTextRole.Error,
                cancellationToken);
            return new RepositoryOpenWorkflowResult(null, null);
        }
    }

    private static RepositoryTrustLevel? ParseInteractiveTrust(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "inspect" or "inspection" or "untrustedinspection" =>
                RepositoryTrustLevel.UntrustedInspection,
            "read" or "trustedread" => RepositoryTrustLevel.TrustedRead,
            "build" or "trustedbuild" => RepositoryTrustLevel.TrustedBuild,
            "mutation" or "trustedmutation" => RepositoryTrustLevel.TrustedMutation,
            _ => null,
        };
    }

    private async Task<InteractiveDecisionResult> HandleDecisionAsync(
        TuiController controller,
        InteractiveDecision decision,
        CancellationToken cancellationToken)
    {
        if (decision.Kind == InteractiveDecisionKind.Plan)
        {
            await _surface.WriteAsync(
                "Plan review: 1 approve, 2 reject, 3 revise, 4 cancel run\n",
                TuiTextRole.Status,
                cancellationToken);
            var input = await _surface.ReadAsync(cancellationToken);
            switch (input.IsSubmitted ? input.Text.Trim() : "4")
            {
                case "1":
                    return await controller.ApproveActivePlanAndProposeMutationSetAsync(
                        cancellationToken) is not null
                            ? InteractiveDecisionResult.AwaitingMutationReview
                            : InteractiveDecisionResult.ContinueWaiting;
                case "2":
                    await _surface.WriteAsync(
                        "Rejection reason:\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    var reason = await _surface.ReadAsync(cancellationToken);
                    _ = reason.IsSubmitted && !string.IsNullOrWhiteSpace(reason.Text)
                        ? await controller.RejectActivePlanAsync(reason.Text, cancellationToken)
                        : await controller.RejectActivePlanAsync(
                            "Rejected from the interactive plan review.",
                            cancellationToken);
                    return InteractiveDecisionResult.ContinueWaiting;
                case "3":
                    await _surface.WriteAsync(
                        "Revision instructions:\n",
                        TuiTextRole.Status,
                        cancellationToken);
                    var revision = await _surface.ReadAsync(cancellationToken);
                    if (revision.IsSubmitted && !string.IsNullOrWhiteSpace(revision.Text))
                    {
                        _ = await controller.ReviseActivePlanAsync(
                            revision.Text,
                            cancellationToken);
                        return InteractiveDecisionResult.ContinueWaiting;
                    }

                    _ = await controller.CancelActiveRunAsync(CancellationToken.None);
                    return InteractiveDecisionResult.ContinueWaiting;
                default:
                    _ = await controller.CancelActiveRunAsync(CancellationToken.None);
                    return InteractiveDecisionResult.ContinueWaiting;
            }
        }

        var mutationSetId = decision.MutationSetId
            ?? throw new InvalidOperationException("The mutation review has no mutation set.");
        var approvalId = decision.ApprovalId
            ?? throw new InvalidOperationException("The mutation review has no approval id.");
        var staged = await controller.LoadMutationReviewAsync(
            mutationSetId,
            cancellationToken);
        if (staged.ApprovalId != approvalId
            || staged.MutationSet.RequiredApproval != decision.RequiredApproval)
        {
            throw new InvalidDataException(
                "The staged mutation review does not match its review-ready event.");
        }

        if (decision.RequiredApproval == MutationApprovalLevel.PolicyAutoApproved)
        {
            _ = await controller.CommitMutationSetAsync(
                mutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.PolicyAutoApproved,
                    ApprovalId = approvalId,
                },
                cancellationToken);
            var validationRunId = controller.BackgroundValidationRunId
                ?? throw new InvalidOperationException("The applied mutation has no validation run.");
            var continuation = await StartPostApplyValidationAsync(
                controller,
                validationRunId,
                cancellationToken);
            return continuation?.Phase == ExecutionCheckpointPhase.MutationApprovalPending
                ? InteractiveDecisionResult.AwaitingMutationReview
                : InteractiveDecisionResult.ContinueWaiting;
        }

        await _surface.WriteAsync(
            "Mutation review: 1 apply approved set, 2 discard\n",
            TuiTextRole.Status,
            cancellationToken);
        var mutationInput = await _surface.ReadAsync(cancellationToken);
        if (mutationInput.IsSubmitted && mutationInput.Text.Trim() == "1")
        {
            _ = await controller.CommitMutationSetAsync(
                mutationSetId,
                new MutationApproval
                {
                    Level = MutationApprovalLevel.EntireSet,
                    ApprovalId = approvalId,
                },
                cancellationToken);
            var validationRunId = controller.BackgroundValidationRunId
                ?? throw new InvalidOperationException("The applied mutation has no validation run.");
            var continuation = await StartPostApplyValidationAsync(
                controller,
                validationRunId,
                cancellationToken);
            return continuation?.Phase == ExecutionCheckpointPhase.MutationApprovalPending
                ? InteractiveDecisionResult.AwaitingMutationReview
                : InteractiveDecisionResult.ContinueWaiting;
        }

        _ = await controller.RollbackMutationSetAsync(mutationSetId, cancellationToken);
        _ = await controller.CancelActiveRunAsync(CancellationToken.None);
        return InteractiveDecisionResult.ContinueWaiting;
    }

    private async Task<ExecutionContinuation?> StartPostApplyValidationAsync(
        TuiController controller,
        RunId runId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(controller);
        var started = _timeProvider.GetTimestamp();
        var startSegments = FormatPostApplyValidationStartSegments(_validationStages);
        if (startSegments is not null)
        {
            await _surface.WriteSegmentsAsync(startSegments, cancellationToken);
        }

        return await ObservePostApplyValidationAsync(controller, runId, started, cancellationToken);
    }

    private async Task<ExecutionContinuation?> ObservePostApplyValidationAsync(
        TuiController controller,
        RunId runId,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        try
        {
            var continuation = await controller.ResumeAppliedMutationValidationAsync(
                runId,
                cancellationToken);
            var suffix = _displayOptions.ShowOperationDurations
                && OperationDurationFormatter.FormatElapsed(_timeProvider, startedTimestamp) is { } elapsed
                    ? $" ({elapsed})"
                    : string.Empty;
            (var message, var role) = FormatPostApplyValidationResult(
                continuation.Phase,
                suffix);
            await _surface.WriteAsync(message, role, CancellationToken.None);
            return continuation;
        }
        catch (OperationCanceledException)
        {
            await _surface.WriteAsync(
                "Validation cancelled. Use /validation retry to resume the applied run.\n",
                TuiTextRole.Status,
                CancellationToken.None);
            return null;
        }
        catch (Exception exception)
        {
            await _surface.WriteAsync(
                FormatStatusError(exception)
                + " Use /validation retry to resume the applied run."
                + Environment.NewLine,
                TuiTextRole.Error,
                CancellationToken.None);
            return null;
        }
    }

    private async Task HandleReasoningCommandAsync(
        string commandText,
        CancellationToken cancellationToken)
    {
        if (_modelCatalog is null || _sessionPreferences is null)
        {
            await _surface.WriteAsync(
                "No model is configured.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var activeSelection = !_activeModelSelectionAvailable
            ? null
            : await _presenter.GetActiveModelSelectionAsync(cancellationToken);
        var activeProfileId = activeSelection?.Profile.Id
            ?? _sessionPreferences.CurrentProfileId
            ?? _startupProfileId;
        var activeProfile = activeSelection?.Profile
            ?? (activeProfileId is null ? null : _modelCatalog.Get(activeProfileId.Value));

        if (activeProfile is null || activeProfileId is null)
        {
            await _surface.WriteAsync(
                "No model is configured.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var levels = activeProfile.SupportedReasoningLevels
            .Select(level => level.ToString().ToLowerInvariant());
        var supportedList = string.Join(", ", levels);
        var capability = activeProfile.ReasoningCapability;

        if (commandText.Length == 10)
        {
            var control = capability.Controllability switch
            {
                ReasoningControllability.Selectable => $"selectable ({supportedList})",
                ReasoningControllability.AlwaysOn => "always on (not user-controllable)",
                _ => "unsupported",
            };
            await _surface.WriteAsync(
                $"Model: {activeProfile.Name}\n"
                + $"Reasoning control: {control}\n"
                + $"Reasoning levels: {supportedList}\n"
                + $"Current: {(activeSelection?.ReasoningLevel
                    ?? _sessionPreferences.ResolveFor(activeProfileId)).ToString().ToLowerInvariant()}\n",
                TuiTextRole.Status,
                cancellationToken);
            return;
        }

        if (capability.Controllability != ReasoningControllability.Selectable)
        {
            await _surface.WriteAsync(
                capability.Controllability == ReasoningControllability.AlwaysOn
                    ? $"Model '{activeProfile.Name}' reasoning is always on and cannot be changed.\n"
                    : $"Model '{activeProfile.Name}' does not support reasoning.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var arg = commandText[10..].Trim();
        if (!Enum.TryParse<ReasoningLevel>(arg, ignoreCase: true, out var level))
        {
            await _surface.WriteAsync(
                $"Unknown reasoning level '{arg}'. Supported: {supportedList}.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        if (!activeProfile.SupportsReasoningLevel(level))
        {
            await _surface.WriteAsync(
                $"Model '{activeProfile.Name}' does not support reasoning level '{arg}'."
                + $" Supported: {supportedList}.\n",
                TuiTextRole.Error,
                cancellationToken);
            return;
        }

        var result = !_activeModelSelectionAvailable
            ? null
            : await _presenter.SetActiveReasoningAsync(level, cancellationToken);
        if (result is null)
        {
            _sessionPreferences.SetReasoning(activeProfileId.Value, level);
        }

        var persistence = result is { Persisted: false }
            ? " The session changed, but restart persistence failed."
            : string.Empty;
        await _surface.WriteAsync(
            $"Reasoning set to {level.ToString().ToLowerInvariant()} for {activeProfile.Name}.{persistence}\n",
            result is { Persisted: false } ? TuiTextRole.Error : TuiTextRole.Status,
            cancellationToken);
    }

    private static string FormatStatusError(Exception exception)
    {
        var message = exception.Message.ReplaceLineEndings(" ");
        return message.Length <= 160 ? message : $"{message[..157]}...";
    }
}
