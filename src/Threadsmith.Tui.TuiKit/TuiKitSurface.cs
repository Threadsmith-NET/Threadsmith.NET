namespace Threadsmith.Tui.TuiKit;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using TUIKit;
using TUIKit.Content;
using TUIKit.Hosting;
using TUIKit.Input;
using TUIKit.Layout;
using TUIKit.Modals;
using TUIKit.Terminal;
using TUIKit.Widgets;

/// <summary>Projects shared interactions through one retained UI loop and one input owner.</summary>
internal sealed class TuiKitSurface : IInteractionSurface, IAsyncDisposable
{
    /// <summary>
    /// Default prompt shown before the coordinator supplies repository context.
    /// </summary>
    internal const string DefaultPrompt = "Threadsmith > ";

    private static readonly string[] _keyHelpEntries =
    [
        "F3 - command palette (empty draft or partial slash command)",
        "Slash prefix - suggestions; arrows choose; Tab/Enter insert only",
        "Completion - Enter again submits; Esc dismisses suggestions",
        "F7 — switch controls between the message editor and output",
        "Output keys — arrows scroll; Shift+arrows select text",
        "F8 — show output links; Enter copies the selected address",
        "Ctrl+C — copy selected text; exit Threadsmith when none is selected",
        "F6 — copy the current selection without using Ctrl+C",
        "Ctrl+Shift+C — copy the complete message draft",
        "F12 — let the terminal select text; press again to return mouse control",
        "Ctrl+Enter — insert a line break without sending",
        "Ctrl+T — toggle thinking when the conversation composer is empty",
        "Esc — close this help",
    ];

    private readonly ITerminalBackend _backend;
    private readonly TuiApplication _app;
    private readonly Channel<Update> _updates = Channel.CreateBounded<Update>(new BoundedChannelOptions(64)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });

    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TuiKitComposer _ordinary = new();
    private readonly TuiKitComposer _secondary = new();
    private readonly TuiKitComposer _steering = new();
    private readonly TranscriptView _transcript = new();
    private readonly CachedTextRun _terminalTooSmall = new();
    private readonly Action _drain;
    private readonly Action _interrupt;
    private readonly bool _suppressStyles;
    private readonly TuiKitCommandDiscovery _discovery;
    private readonly ComposerCommandCompletion _completion;
    private readonly ComposerAutocomplete _autocomplete;
    private CommandPaletteModal? _paletteModal;
    private ComposerCompletionTarget? _paletteTarget;
    private TuiKitComposer _composer;
    private TuiKitStyles _styles;
    private ConfiguredTheme _theme;
    private string? _queuedOrdinaryInput;
    private Task? _loop;
    private Task _clipboardRead = Task.CompletedTask;
    private Task _utilityModal = Task.CompletedTask;
    private TaskCompletionSource<InteractionInput>? _read;
    private SessionStatusSnapshot? _status;
    private SessionStatusSnapshot? _formattedStatus;
    private InteractionActivity? _activity;
    private ActiveInputLease? _activeInput;
    private string _prompt = DefaultPrompt;
    private string _notice = string.Empty;
    private string _statusText = string.Empty;
    private string? _statusSeparator;
    private string _activityText = string.Empty;
    private int _statusWidth = -1;
    private int _activityFrame = -1;
    private long _inputEpoch;
    private int _interrupted;
    private int _disposed;
    private InteractionActivity? _formattedActivity;
    private bool _activityQueued;

    /// <summary>Initializes a new instance of the <see cref="TuiKitSurface"/> class.</summary>
    internal TuiKitSurface(ConfiguredTheme theme, Action interrupt, ITerminalBackend? backend = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(interrupt);
        _drain = Drain;
        _backend = backend ?? new ConsoleBackend();
        if (backend is null && (!_backend.IsInteractive
            || string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase)))
        {
            _backend.Dispose();
            throw new UnsupportedTerminalException("--tui=tuikit requires an interactive terminal with cursor support.");
        }

        _theme = theme;
        _interrupt = interrupt;
        _suppressStyles = TuiThemeResolver.ShouldSuppressStyles(false, Environment.GetEnvironmentVariable("NO_COLOR"), Environment.GetEnvironmentVariable("TERM"));
        _styles = new TuiKitStyles(theme, _suppressStyles);
        _composer = _ordinary;
        _discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, CompleteCommand);
        _completion = new ComposerCommandCompletion(_discovery);
        _autocomplete = new ComposerAutocomplete(_discovery, _completion, CompleteCommand);
        _app = new TuiApplication(_backend)
        {
            CtrlCPolicy = CtrlCPolicy.Custom,
            TargetFps = 30,
            Layout = new LayoutBuilder().DockBottom("status", 1).DockBottom("composer", 4)
                .DockBottom("activity", 1).Fill("transcript").Build(),
        };
        _transcript.ResolveStyle = ResolveStyle;
        _app.Bind("transcript", _transcript);
        _app.Bind("composer", new ComposerPane(this));
        _app.Bind("activity", new TextRow(
            ActivityText,
            () => _styles.Resolve(_activity?.Label == "THINKING" ? PresentationTextRole.ThinkingIndicator : PresentationTextRole.Status)));
        _app.Bind("status", new TextRow(
            StatusText,
            () => _status is null ? CellStyle.Default : _styles.Resolve(PresentationTextRole.SessionStatus)));
        foreach (var composer in new[] { _ordinary, _secondary, _steering })
        {
            composer.CopyRequested = Copy;
            composer.PasteRequested = () => RequestPaste(null);
        }

        _app.Focus("composer");
        _app.FocusChanged += _ => RefreshDiscovery();
        _app.KeyFilter = HandleKey;
        _app.PasteReceived += Paste;
        _app.RenderOverlay = surface =>
        {
            _app.Post(_drain);
            RefreshDiscovery();
            if (!ModalFrame.Fits(surface.Size))
            {
                surface.Fill(new Rect(0, 0, surface.Size.Width, surface.Size.Height), Cell.Blank(CellStyle.Default));
                if (surface.Size.Width > 0 && surface.Size.Height > 0)
                {
                    _terminalTooSmall.Draw(surface, 0, 0, "Terminal too small: need 40 x 12", CellStyle.Default);
                }
            }
            else if (surface is BufferSurface root
                && _app.Layout.FindById("composer") is { } composerRegion
                && _app.Layout.FindById("activity") is { } activityRegion)
            {
                _autocomplete.Render(
                    root,
                    composerRegion.ContentRect(surface.Size),
                    activityRegion.ContentRect(surface.Size),
                    _composer.VisibleCaret,
                    ResolveStyle(PresentationTextRole.Default),
                    ResolveStyle(PresentationTextRole.SelectionHighlight));
            }

            // TUIKit streams a complete first frame through the bottom-right cell and relies on
            // terminal autowrap between rows. Some terminals scroll as that final cell is written,
            // moving the status bar up and exposing a blank last row. An orphan continuation is
            // retained in the buffer but omitted by the renderer, preventing that terminal scroll.
            if (surface.Size.Width > 0 && surface.Size.Height > 0)
            {
                surface.Set(
                    surface.Size.Width - 1,
                    surface.Size.Height - 1,
                    Cell.Continuation(CellStyle.Default));
            }
        };
        _app.Post(_drain);
    }

    /// <inheritdoc />
    public InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsActiveRunInput: true, SupportsRetainedStatus: true);

    private ComposerPurpose CurrentPurpose => ReferenceEquals(_composer, _ordinary)
        ? ComposerPurpose.Conversation : ReferenceEquals(_composer, _secondary) ? ComposerPurpose.Secondary : ComposerPurpose.Steering;

    private bool DiscoveryEnabled => _app.FocusedRegion == "composer" && !_app.Modals.IsActive
        && ModalFrame.Fits(_backend.Size) && !_stop.IsCancellationRequested;

    /// <inheritdoc />
    public async Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var completion = new TaskCompletionSource<InteractionInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        await EnqueueAsync(
            () =>
        {
            if (_read is not null)
            {
                throw new InvalidOperationException("Only one composer read may own input.");
            }

            _read = completion;
            ClosePalette();
            _prompt = request.Prompt;
            _composer.OnFocusChanged(false);
            _composer = request.Purpose switch { ComposerPurpose.Secondary => _secondary, ComposerPurpose.Steering => _steering, _ => _ordinary };
            if (request.Purpose != ComposerPurpose.Conversation)
            {
                _composer.Text = string.Empty;
            }

            _composer.OnFocusChanged(true);
            _inputEpoch++;
            _app.Focus("composer");
            _notice = "Ctrl+C copy selected text (exits if none) | Ctrl+Enter add line break | F1 explain all shortcuts";
            if (request.Purpose == ComposerPurpose.Conversation
                && _queuedOrdinaryInput is { } queuedInput)
            {
                _queuedOrdinaryInput = null;
                _read.TrySetResult(new InteractionInput(true, queuedInput, _stop.Token));
            }
        },
            cancellationToken);
        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(
                    () =>
                {
                    if (ReferenceEquals(_read, completion))
                    {
                        _read = null;
                    }

                    _composer.OnFocusChanged(false);
                    ClosePalette();
                    _composer = _ordinary;
                    _prompt = DefaultPrompt;
                    _composer.OnFocusChanged(true);
                    _inputEpoch++;
                },
                    _stop.Token);
            }
        }
    }

    /// <inheritdoc />
    public async Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Options.Count == 0 || request.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
        {
            throw new ArgumentException("Selections require distinct, stable options.", nameof(request));
        }

        var modal = new ChoiceModal(request.Title, request.Options.Select(option => new Choice(option.Id, option.Label)).ToArray())
        {
            ResolveStyle = ResolveStyle,
            SelectionMarker = _theme.Ui.SelectionMarker,
            ToggleMouse = () => _app.ToggleMouseCapture(),
            CopyRequested = Copy,
        };
        modal.PasteRequested = () => RequestPaste(modal, () => modal.FilterText);
        Task<string?>? result = null;
        await EnqueueAsync(
            () =>
        {
            ClosePalette();
            result = _app.ShowAsync<string>(modal);
        },
            cancellationToken);
        try
        {
            var selected = await (result ?? throw new InvalidOperationException("Selection did not open.")).WaitAsync(cancellationToken);
            return new InteractionSelectionResult(selected, selected is null);
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(() => modal.RequestClose(null), _stop.Token);
            }
        }
    }

    /// <inheritdoc />
    public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return EnqueueAsync(() => _transcript.Present(batch), cancellationToken);
    }

    /// <inheritdoc />
    public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        return EnqueueAsync(() => _status = status, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(operation);
        await EnqueueAsync(() => _activity = activity, cancellationToken);
        try
        {
            await operation.WaitAsync(cancellationToken);
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(
                    () =>
                    {
                        if (ReferenceEquals(_activity, activity))
                        {
                            _activity = null;
                        }
                    },
                    _stop.Token);
            }
        }
    }

    /// <inheritdoc />
    public IActiveRunInputLease? BeginActiveRunInput(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        var lease = new ActiveInputLease(this, timeProvider);
        if (Interlocked.CompareExchange(ref _activeInput, lease, null) is not null)
        {
            throw new InvalidOperationException("An active-run input lease is already held.");
        }

        return lease;
    }

    /// <inheritdoc />
    [SuppressMessage("Usage", "VSTHRD003", Justification = "Both owned tasks run without a synchronization context; disposal joins them after cancellation.")]
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync();
        try
        {
            if (_loop is not null)
            {
                try
                {
                    await _loop.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                }
            }
            else
            {
                _app.Dispose();
            }
        }
        finally
        {
            _autocomplete.Dispose();
            try
            {
                await Task.WhenAll(_clipboardRead, _utilityModal).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            finally
            {
                _stop.Dispose();
            }
        }
    }

    /// <summary>Runs one UI owner alongside the shared coordinator and restores the terminal.</summary>
    internal async Task RunAsync(Func<CancellationToken, Task> coordinate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinate);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        _loop = Task.Run(() => RunLoopAsync(lifetime.Token), CancellationToken.None);
        try
        {
            await _started.Task.WaitAsync(lifetime.Token);
            var coordination = coordinate(lifetime.Token);
            if (await Task.WhenAny(coordination, _loop) == _loop)
            {
                await lifetime.CancelAsync();
                try
                {
                    await coordination;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }

                await _loop.ConfigureAwait(false);
            }
            else
            {
                await coordination;
            }
        }
        finally
        {
            await lifetime.CancelAsync();
            await DisposeAsync();
        }
    }

    /// <summary>Applies a theme on the UI owner, including retained transcript content.</summary>
    internal Task SetThemeAsync(ConfiguredTheme theme, CancellationToken cancellationToken)
    {
        return EnqueueAsync(
        () =>
    {
        _theme = theme;
        _styles = new TuiKitStyles(theme, _suppressStyles);
    },
        cancellationToken);
    }

    /// <summary>Releases the active-run input owner when its lease is disposed.</summary>
    internal void ReleaseActiveInput(ActiveInputLease lease)
    {
        Interlocked.CompareExchange(ref _activeInput, null, lease);
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _app.Start();
            _started.TrySetResult();
            await _app.RunAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            _started.TrySetException(exception);
            throw;
        }
        finally
        {
            _updates.Writer.TryComplete();
            while (_updates.Reader.TryRead(out var update))
            {
                update.Completion.TrySetCanceled(cancellationToken);
            }

            _read?.TrySetCanceled(cancellationToken);
            ClosePalette();
            _app.Modals.Top?.RequestClose(null);
            _app.Stop();
            _app.Dispose();
            await _stop.CancelAsync();
        }
    }

    [SuppressMessage("Usage", "VSTHRD003", Justification = "The acknowledgement is completed asynchronously by the dedicated terminal loop, never by a caller context.")]
    private async Task EnqueueAsync(Action mutation, CancellationToken cancellationToken)
    {
        var update = new Update(mutation, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        try
        {
            await _updates.Writer.WriteAsync(update, cancellationToken);
        }
        catch (ChannelClosedException exception)
        {
            throw new OperationCanceledException("The TUIKit presentation loop has stopped.", exception, _stop.Token);
        }

        // Cancellation affects admission; an accepted operation completes in FIFO order.
        await update.Completion.Task.ConfigureAwait(false);
    }

    private void Drain()
    {
        for (var count = 0; count < 32 && _updates.Reader.TryRead(out var update); count++)
        {
            try
            {
                update.Mutation();
                RefreshDiscovery();
                update.Completion.TrySetResult();
            }
            catch (Exception exception)
            {
                update.Completion.TrySetException(exception);
                throw;
            }
        }
    }

    private bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        RefreshDiscovery();
        var lease = Volatile.Read(ref _activeInput);
        if (key.Code != KeyCode.Escape)
        {
            lease?.DisarmEscape();
        }

        if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            HandleControlC();
            return true;
        }

        if (key.Code == KeyCode.F12)
        {
            _app.ToggleMouseCapture();
            _notice = _app.MouseCaptureEnabled ? "Mouse: application selection; F12 for terminal selection" : "Mouse: terminal selection; F12 to return";
            return true;
        }

        if (!ModalFrame.Fits(_backend.Size))
        {
            return true;
        }

        if (key.Code == KeyCode.F1)
        {
            if (_utilityModal.IsCompleted)
            {
                _utilityModal = ShowKeyHelpAsync(_stop.Token);
            }

            return true;
        }

        if (key.Code == KeyCode.F3 && key.Modifiers == KeyModifiers.None)
        {
            if (_utilityModal.IsCompleted && DiscoveryEnabled
                && _completion.Capture(_composer.Buffer, CurrentPurpose, _inputEpoch, allowEmpty: true) is { } target)
            {
                _paletteTarget = target;
                var modal = new CommandPaletteModal(
                    _discovery,
                    string.Empty,
                    () => _backend.Size,
                    ResolveStyle,
                    HandleControlC,
                    () => _app.ToggleMouseCapture(),
                    () => Volatile.Read(ref _activeInput)?.DisarmEscape())
                {
                    CopyRequested = Copy,
                };
                modal.PasteRequested = () => RequestPaste(modal, () => modal.FilterText);
                _paletteModal = modal;
                _utilityModal = ShowUtilityModalAsync(modal, _stop.Token);
            }
            else if (_utilityModal.IsCompleted && DiscoveryEnabled)
            {
                _notice = "F3 requires an empty ordinary draft or partial slash command";
            }

            return true;
        }

        if (key.Code == KeyCode.F8)
        {
            if (_utilityModal.IsCompleted)
            {
                _utilityModal = SelectLinkAsync(_stop.Token);
            }

            return true;
        }

        if (key.Code == KeyCode.F7)
        {
            _app.Focus(_app.FocusedRegion == "composer" ? "transcript" : "composer");
            return true;
        }

        if (key.Code == KeyCode.F6 || (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift)))
        {
            Copy(_app.FocusedRegion == "transcript" ? _transcript.SelectedText() : key.Code == KeyCode.F6 ? _composer.Buffer.Selection : _composer.Text);
            return true;
        }

        if (_autocomplete.HandleKey(key))
        {
            lease?.DisarmEscape();
            return true;
        }

        if (key.Code == KeyCode.Escape && key.Modifiers == KeyModifiers.None)
        {
            if (_read is not null)
            {
                _read.TrySetResult(new InteractionInput(false, string.Empty, _stop.Token));
            }
            else
            {
                lease?.Escape();
            }

            return true;
        }

        if (_app.FocusedRegion != "composer")
        {
            return false;
        }

        if (key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.Ctrl)
        {
            if (key.Rune == 'l')
            {
                _transcript.ClearView();
                return true;
            }

            if (key.Rune == 't')
            {
                if (_read is not null && ReferenceEquals(_composer, _ordinary) && _composer.Text.Length == 0)
                {
                    _read.TrySetResult(new InteractionInput(true, string.Empty, _stop.Token, InteractionInputKind.ToggleThinking));
                }

                return true;
            }
        }

        switch (TuiKitInput.ResolveSubmit(key))
        {
            case SubmitDecision.InsertNewline:
                _composer.InsertText("\n");
                RefreshDiscovery();
                return true;
            case SubmitDecision.Submit:
                if (_read is null)
                {
                    if (lease is not null)
                    {
                        lease.Steer();
                    }
                    else if (ReferenceEquals(_composer, _ordinary)
                        && _queuedOrdinaryInput is null
                        && _composer.Text.Length > 0)
                    {
                        var queuedInput = _composer.Text;
                        _queuedOrdinaryInput = queuedInput;
                        CommitComposerInput(queuedInput);
                        _notice = "Message queued until the semantic model is ready";
                    }

                    return true;
                }

                var input = new InteractionInput(true, _composer.Text, _stop.Token);
                if (_read.TrySetResult(input))
                {
                    CommitComposerInput(input.Text);
                }

                return true;
            default:
                return false;
        }
    }

    private void CommitComposerInput(string text)
    {
        if (ReferenceEquals(_composer, _ordinary) && text.Length > 0)
        {
            _transcript.Present(new PresentationBatch([
                new PresentationTextItem([
                    new(_prompt, PresentationTextRole.ComposerPrompt),
                    new(text, PresentationTextRole.UserPrompt),
                    new("\n", PresentationTextRole.UserPrompt),
                ]),
            ]));
        }

        _composer.History.Add(text);
        _composer.Text = string.Empty;
        _inputEpoch++;
        RefreshDiscovery();
    }

    private void RefreshDiscovery()
    {
        if (_paletteModal?.IsClosed == true)
        {
            _paletteModal = null;
            _paletteTarget = null;
        }

        _autocomplete.Refresh(_composer.Buffer, CurrentPurpose, _inputEpoch, DiscoveryEnabled);
    }

    private void CompleteCommand(string name)
    {
        var target = _paletteTarget ?? _autocomplete.Target;
        if (target is null || _app.FocusedRegion != "composer" || !ModalFrame.Fits(_backend.Size))
        {
            return;
        }

        try
        {
            _completion.TryApply(target, _composer.Buffer, CurrentPurpose, _inputEpoch, name);
        }
        catch (Exception exception) when (exception is InvalidOperationException or EncoderFallbackException or DecoderFallbackException)
        {
            _notice = "Completion exceeds the draft limit or contains invalid Unicode; draft preserved";
        }

        RefreshDiscovery();
    }

    private void ClosePalette()
    {
        _paletteModal?.RequestClose(null);
        _app.Modals.RemoveClosed();
        _paletteModal = null;
        _paletteTarget = null;
    }

    private Task ShowKeyHelpAsync(CancellationToken cancellationToken)
    {
        var modal = new KeyHelpModal("Key help - PgUp/PgDn scroll; Esc closes", _keyHelpEntries)
        {
            ResolveStyle = ResolveStyle,
            ToggleMouse = () => _app.ToggleMouseCapture(),
        };
        return ShowUtilityModalAsync(modal, cancellationToken);
    }

    private async Task ShowUtilityModalAsync(Modal modal, CancellationToken cancellationToken)
    {
        try
        {
            var result = _app.ShowAsync<string>(modal);
            RefreshDiscovery();
            _ = await result.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown closes presentation-only discovery and help.
        }
        catch (ChannelClosedException) when (_stop.IsCancellationRequested)
        {
            // The UI owner has already shut down.
        }
    }

    private async Task SelectLinkAsync(CancellationToken cancellationToken)
    {
        var links = _transcript.Links;
        if (links.Count == 0)
        {
            _notice = "No links in retained output";
            return;
        }

        var modal = new ChoiceModal("Links: Enter copies the target; F2 shows full details", links.Select(uri => new Choice(uri.AbsoluteUri, uri.AbsoluteUri)).ToArray())
        {
            ResolveStyle = ResolveStyle,
            SelectionMarker = _theme.Ui.SelectionMarker,
            ToggleMouse = () => _app.ToggleMouseCapture(),
            CopyRequested = Copy,
        };
        modal.PasteRequested = () => RequestPaste(modal, () => modal.FilterText);
        try
        {
            var result = _app.ShowAsync<string>(modal);
            RefreshDiscovery();
            var selected = await result.WaitAsync(cancellationToken);
            if (selected is not null)
            {
                await EnqueueAsync(() => Copy(selected), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown closes the presentation-only link picker.
        }
        catch (ChannelClosedException) when (_stop.IsCancellationRequested)
        {
            // The UI owner has already shut down.
        }
    }

    private string ActivityText()
    {
        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        if (_activity is null)
        {
            _formattedActivity = null;
            _activityFrame = -1;
            return AppendUnseenOutput(_notice);
        }

        var frame = (int)((Environment.TickCount64 / 250) % frames.Length);
        var queued = _queuedOrdinaryInput is not null;
        if (!ReferenceEquals(_formattedActivity, _activity) || frame != _activityFrame || queued != _activityQueued)
        {
            _formattedActivity = _activity;
            _activityFrame = frame;
            _activityQueued = queued;
            var activity = $"{frames[frame]} {_activity.Format()}";
            _activityText = queued ? $"{activity} | message queued" : activity;
        }

        return AppendUnseenOutput(_activityText);
    }

    private string AppendUnseenOutput(string text)
    {
        return _transcript.NewCount == 0
            ? text
            : $"{text}{(text.Length == 0 ? string.Empty : " | ")}{_transcript.NewCount} new output update(s) — End to follow";
    }

    private string StatusText()
    {
        if (_status is null)
        {
            return string.Empty;
        }

        var width = _backend.Size.Width;
        var separator = _theme.Ui.FooterSeparator;
        if (!ReferenceEquals(_status, _formattedStatus) || width != _statusWidth || separator != _statusSeparator)
        {
            _formattedStatus = _status;
            _statusWidth = width;
            _statusSeparator = separator;
            _statusText = TuiSessionStatusFormatter.Format(_status, width, separator);
        }

        return _statusText;
    }

    private CellStyle ResolveStyle(PresentationTextRole role)
    {
        return _styles.Resolve(role);
    }

    private void HandleControlC()
    {
        var transcriptSelection = _transcript.SelectedText();
        var composerSelection = _composer.Buffer.Selection;
        var focusedSelection = _app.FocusedRegion == "transcript" ? transcriptSelection : composerSelection;
        var remainingSelection = _app.FocusedRegion == "transcript" ? composerSelection : transcriptSelection;
        var selection = focusedSelection.Length > 0 ? focusedSelection : remainingSelection;
        if (selection.Length > 0)
        {
            _ = Copy(selection);
            return;
        }

        if (Interlocked.Exchange(ref _interrupted, 1) == 0)
        {
            _interrupt();
        }
    }

    private bool Copy(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        if (Encoding.UTF8.GetByteCount(text) > 64 * 1024)
        {
            _notice = "Copy exceeds 64 KiB; select a smaller range";
            return false;
        }

        _backend.Write(ClipboardWriter.BuildSequence(text));
        _backend.Flush();
        _notice = "Selection copied; Ctrl+C cancels only when nothing is selected";
        return true;
    }

    private void Paste(string text)
    {
        Volatile.Read(ref _activeInput)?.DisarmEscape();
        if (!ModalFrame.Fits(_backend.Size))
        {
            return;
        }

        if (_app.Modals.IsActive)
        {
            _app.Modals.HandlePaste(text);
            return;
        }

        try
        {
            _composer.InsertText(text);
            RefreshDiscovery();
        }
        catch (Exception exception) when (exception is InvalidOperationException or EncoderFallbackException or DecoderFallbackException)
        {
            _notice = "Paste exceeds the 1 MiB limit or contains invalid Unicode; draft preserved";
        }
    }

    private void RequestPaste(Modal? modal, Func<string>? filterText = null)
    {
        if (_clipboardRead.IsCompleted)
        {
            _clipboardRead = PasteClipboardAsync(modal, filterText, _composer, _inputEpoch, _stop.Token);
        }
    }

    private async Task PasteClipboardAsync(Modal? modal, Func<string>? filterText, TuiKitComposer composer, long epoch, CancellationToken cancellationToken)
    {
        var filter = filterText?.Invoke();
        try
        {
            var text = await ClipboardReader.ReadAsync(cancellationToken);
            await EnqueueAsync(
                () =>
            {
                if (text is null)
                {
                    _notice = "Clipboard unavailable; use your terminal's paste shortcut";
                }
                else if (modal is not null && !modal.IsClosed && ReferenceEquals(_app.Modals.Top, modal) && filterText?.Invoke() == filter)
                {
                    modal.HandlePaste(text);
                }
                else if (modal is null && !_app.Modals.IsActive && ReferenceEquals(_composer, composer) && epoch == _inputEpoch)
                {
                    Paste(text);
                }
                else
                {
                    _notice = "Paste cancelled: input destination changed";
                }
            },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ChannelClosedException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or DecoderFallbackException or InvalidOperationException or Win32Exception)
        {
            if (!_stop.IsCancellationRequested)
            {
                _app.Post(() => _notice = "Clipboard unavailable; draft preserved");
            }
        }
    }

    private readonly record struct Update(Action Mutation, TaskCompletionSource Completion);

    private sealed class ComposerPane : IWidget, IFocusable, IFocusAware, IMouseAware
    {
        private readonly TuiKitSurface _owner;
        private readonly CachedTextRun _prompt = new();

        internal ComposerPane(TuiKitSurface owner)
        {
            _owner = owner;
        }

        public Size Measure(Size available)
        {
            return new(available.Width, Math.Min(4, available.Height));
        }

        public bool HandleKey(KeyEvent key)
        {
            var handled = _owner._composer.HandleKey(key);
            _owner.RefreshDiscovery();
            return handled;
        }

        public void OnFocusChanged(bool focused)
        {
            _owner._composer.OnFocusChanged(focused);
        }

        public bool HandleMouse(MouseEvent mouse)
        {
            if (!ModalFrame.Fits(_owner._backend.Size))
            {
                return true;
            }

            var handled = _owner._composer.HandleMouse(mouse);
            _owner.RefreshDiscovery();
            return handled;
        }

        public void Render(ISurface surface)
        {
            if (surface.Size.Height < 1)
            {
                return;
            }

            var promptWidth = PromptWidth(surface.Size.Width);
            _owner._composer.FirstRowOffset = promptWidth;
            _owner._composer.Style = _owner._styles.Resolve(PresentationTextRole.Default);
            _owner._composer.Render(surface);
            _prompt.Draw(surface, 0, 0, _owner._prompt, _owner._styles.Resolve(PresentationTextRole.ComposerPrompt));
        }

        private int PromptWidth(int availableWidth)
        {
            return Math.Min(UnicodeWidth.GetWidth(_owner._prompt), Math.Max(0, availableWidth - 1));
        }
    }
}
