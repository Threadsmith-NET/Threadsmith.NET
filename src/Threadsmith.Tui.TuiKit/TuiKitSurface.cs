namespace Threadsmith.Tui.TuiKit;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading.Channels;
using Threadsmith.Interaction.Agents;
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
internal sealed partial class TuiKitSurface : IInteractionSurface, IAgentWorkspaceSurface, IInteractionToolActivitySurface, IAsyncDisposable
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
        "F7 — switch MAIN editor/output; child views keep output focus",
        "Ctrl+Left/Right — previous/next agent while output has focus",
        "F2 — selected agent details while output has focus",
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
    private readonly Func<CancellationToken, Task<string?>> _readClipboard;
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
    private readonly AgentViews _agents;
    private readonly AgentTabStrip _tabs;
    private readonly OutputPane _outputPane;
    private readonly ComposerPane _composerPane;
    private Size _layoutSize;

    private TranscriptView SelectedTranscript => _agents.Selected.Transcript;

    private readonly CachedTextRun _terminalTooSmall = new();
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
    private Task? _loop;
    private Task _clipboardRead = Task.CompletedTask;
    private Task _utilityModal = Task.CompletedTask;
    private TaskCompletionSource<InteractionInput>? _read;
    private SessionStatusSnapshot? _status;
    private SessionStatusSnapshot? _formattedStatus;
    private InteractionActivity? _activity;
    private IReadOnlyList<InteractionActivity> _toolActivities = [];
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

    /// <summary>Initializes a new instance of the <see cref="TuiKitSurface"/> class.</summary>
    internal TuiKitSurface(ConfiguredTheme theme, Action interrupt, ITerminalBackend? backend = null, Func<CancellationToken, Task<string?>>? readClipboard = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(interrupt);
        _backend = backend ?? new ConsoleBackend();
        _readClipboard = readClipboard ?? ClipboardReader.ReadAsync;
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
        _agents = new AgentViews(ResolveOutputStyle);
        _tabs = new AgentTabStrip(_agents, SelectAgent, ResolveStyle);
        _outputPane = new OutputPane(this);
        _composerPane = new ComposerPane(this);
        _layoutSize = _backend.Size;
        _discovery = new TuiKitCommandDiscovery(InteractiveCommandCatalog.All, CompleteCommand);
        _completion = new ComposerCommandCompletion(_discovery);
        _autocomplete = new ComposerAutocomplete(_discovery, _completion, CompleteCommand);
        _app = new TuiApplication(_backend)
        {
            CtrlCPolicy = CtrlCPolicy.Custom,
            TargetFps = 30,
            Layout = WorkspaceLayout.Create(_layoutSize),
            EnableMouseRouting = false,
            AutoRenderNotifications = false,
        };
        _app.Bind("title", new TextRow(() => "Threadsmith.NET", () => ResolveStyle(PresentationTextRole.TitleBarRole)));
        _app.Bind("tabs", _tabs);
        _app.Bind("transcript", _outputPane);
        _app.Bind("composer", _composerPane);
        _app.MouseReceived += RouteMouse;
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
                && _app.Layout?.FindById("composer") is { } composerRegion
                && _app.Layout?.FindById("transcript") is { } outputRegion)
            {
                _autocomplete.Render(
                    root,
                    ComposerScreenRect(composerRegion.ContentRect(surface.Size)),
                    OutputScreenRect(outputRegion.ContentRect(surface.Size)),
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
        _app.Interrupted += _interrupt;
    }

    /// <inheritdoc />
    public InteractionSurfaceCapabilities Capabilities { get; } = new(
        SupportsActiveRunInput: true,
        SupportsRetainedStatus: true,
        SupportsRetainedActivity: true,
        SupportsRetainedRunHints: true);

    private ComposerPurpose CurrentPurpose => ReferenceEquals(_composer, _ordinary)
        ? ComposerPurpose.Conversation : ReferenceEquals(_composer, _secondary) ? ComposerPurpose.Secondary : ComposerPurpose.Steering;

    private bool CanEdit => ReferenceEquals(_agents.Selected, _agents.Main) && (!_startupBlocked || _activeInput is not null);

    private bool DiscoveryEnabled => CanEdit && _app.FocusedRegion == "composer" && !_app.Modals.IsActive
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
            _startupBlocked = false;
            _startupDetails = [];
            _startupPhases.Clear();
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
            if (request.Purpose != ComposerPurpose.Conversation)
            {
                SelectAgent(_agents.Main);
            }

            if (CanEdit)
            {
                _app.Focus("composer");
            }

            _notice = string.Empty;
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
        return EnqueueAsync(
            () =>
        {
            _agents.Present(batch);
            if (batch.Target is null)
            {
                foreach (var item in batch.Items.OfType<PresentationTextItem>())
                {
                    if (item.Notification is { } notification)
                    {
                        var severity = notification.Role switch
                        {
                            PresentationTextRole.Success => NotificationSeverity.Success,
                            PresentationTextRole.Warning => NotificationSeverity.Warning,
                            PresentationTextRole.Error => NotificationSeverity.Error,
                            _ => NotificationSeverity.Info,
                        };
                        _app.Notify(TranscriptView.Safe(notification.Text).ReplaceLineEndings(" "), severity);
                    }
                }
            }
        },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        return EnqueueAsync(() => _status = status, cancellationToken);
    }

    /// <inheritdoc />
    public Task PresentToolActivitiesAsync(IReadOnlyList<InteractionActivity> activities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activities);
        var snapshot = activities.ToArray();
        return EnqueueAsync(() => _toolActivities = snapshot, cancellationToken);
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
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_layoutSize != _backend.Size)
                {
                    _layoutSize = _backend.Size;
                    _app.Layout = WorkspaceLayout.Create(_layoutSize);
                    RefreshDiscovery();
                    _app.RenderOnce();
                }

                _app.PumpInputOnce();
                Drain();
                _app.RenderOnce();
                await Task.Delay(TimeSpan.FromMilliseconds(1000d / 30), cancellationToken);
            }
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

        if (_startupBlocked && _activeInput is null)
        {
            return true;
        }

        if (key.Modifiers == KeyModifiers.Ctrl && key.Code is KeyCode.Left or KeyCode.Right
            && _app.FocusedRegion == "transcript")
        {
            if (_agents.Cycle(key.Code == KeyCode.Left ? -1 : 1))
            {
                AgentSelectionChanged();
            }

            return true;
        }

        if (key.Code == KeyCode.F2)
        {
            if (_utilityModal.IsCompleted)
            {
                var modal = new ChoiceModal("Agent details — F2 full text; Esc closes", [new Choice("details", AgentHeader.FormatDetails(GetAgentHeaderState()))])
                {
                    ResolveStyle = ResolveStyle,
                    ToggleMouse = () => _app.ToggleMouseCapture(),
                    CopyRequested = Copy,
                };
                _utilityModal = ShowUtilityModalAsync(modal, _stop.Token);
            }

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
            _app.Focus(_app.FocusedRegion == "composer" || !CanEdit ? "transcript" : "composer");
            return true;
        }

        if (key.Code == KeyCode.F6 || (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == (KeyModifiers.Ctrl | KeyModifiers.Shift)))
        {
            Copy(_app.FocusedRegion == "transcript" ? SelectedTranscript.SelectedText() : key.Code == KeyCode.F6 ? _composer.Buffer.Selection : _composer.Text);
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

        if (!CanEdit)
        {
            return _app.FocusedRegion != "transcript";
        }

        if (_app.FocusedRegion != "composer")
        {
            return false;
        }

        if (key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.Ctrl)
        {
            if (key.Rune == 'l')
            {
                SelectedTranscript.ClearView();
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
                    lease?.Steer();

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
            _agents.Main.Transcript.Present(new PresentationBatch([
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
        if (!CanEdit || target is null || _app.FocusedRegion != "composer" || !ModalFrame.Fits(_backend.Size))
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
        var links = SelectedTranscript.Links;
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
        if (_agents.Selected.Snapshot is { } child)
        {
            return PrependUnseenOutput(child.Activity ?? child.State.ToString());
        }

        const string frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
        if (_activity is null || _toolActivities.Count > 0)
        {
            _formattedActivity = null;
            _activityFrame = -1;
            return ActiveRunNotice(_notice);
        }

        var frame = (int)((Environment.TickCount64 / 250) % frames.Length);
        if (!ReferenceEquals(_formattedActivity, _activity) || frame != _activityFrame)
        {
            _formattedActivity = _activity;
            _activityFrame = frame;
            var activity = $"{frames[frame]} {_activity.Format()}";
            _activityText = activity;
        }

        return ActiveRunNotice(_activityText);
    }

    private string ActiveRunNotice(string text)
    {
        if (Volatile.Read(ref _activeInput) is not null)
        {
            const string hints = "ENTER to steer; ESC-ESC to cancel";
            text = string.IsNullOrEmpty(text) ? hints : $"{text} | {hints}";
        }

        return PrependUnseenOutput(text);
    }

    private string PrependUnseenOutput(string text)
    {
        if (SelectedTranscript.NewCount == 0)
        {
            return text;
        }

        return $"{SelectedTranscript.NewCount} new output{(text.Length == 0 ? string.Empty : " | " + text)}";
    }

    private string StatusText()
    {
        if (_status is null || !_status.FooterEnabled)
        {
            return string.Empty;
        }

        var width = WorkspaceLayout.RowContent(_backend.Size).Width;
        var separator = _theme.Ui.FooterSeparator;
        if (!ReferenceEquals(_status, _formattedStatus) || width != _statusWidth || separator != _statusSeparator)
        {
            _formattedStatus = _status;
            _statusWidth = width;
            _statusSeparator = separator;
            _statusText = RepositoryFooter.Format(_status.Folder, _status.Branch, _status.GitStatus, width, separator);
        }

        return _statusText;
    }

    private CellStyle ResolveStyle(PresentationTextRole role)
    {
        return _styles.Resolve(role);
    }

    private void HandleControlC()
    {
        var transcriptSelection = SelectedTranscript.SelectedText();
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

        if (!CanEdit)
        {
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
        if ((modal is not null || CanEdit) && _clipboardRead.IsCompleted)
        {
            _clipboardRead = PasteClipboardAsync(modal, filterText, _composer, _inputEpoch, _stop.Token);
        }
    }

    private async Task PasteClipboardAsync(Modal? modal, Func<string>? filterText, TuiKitComposer composer, long epoch, CancellationToken cancellationToken)
    {
        var filter = filterText?.Invoke();
        try
        {
            var text = await _readClipboard(cancellationToken);
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
                else if (CanEdit && modal is null && !_app.Modals.IsActive && ReferenceEquals(_composer, composer) && epoch == _inputEpoch)
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
            if (!_owner.CanEdit || _owner._app.Modals.IsActive)
            {
                return true;
            }

            var handled = _owner._composer.HandleKey(key);
            _owner.RefreshDiscovery();
            return handled;
        }

        public void OnFocusChanged(bool focused)
        {
            _owner._composer.OnFocusChanged(focused && _owner.CanEdit);
        }

        public bool HandleMouse(MouseEvent mouse)
        {
            if (!_owner.CanEdit || _owner._app.Modals.IsActive || !ModalFrame.Fits(_owner._backend.Size))
            {
                return true;
            }

            var region = _owner._app.Layout?.FindById("composer")?.ContentRect(_owner._backend.Size) ?? default;
            var rect = WorkspaceLayout.ComposerContent(new Size(region.Width, region.Height));
            var handled = _owner._composer.HandleMouse(WorkspaceLayout.Translate(mouse, rect));
            _owner.RefreshDiscovery();
            return handled;
        }

        public void Render(ISurface surface)
        {
            if (surface.Size.Height < 1)
            {
                return;
            }

            var background = _owner.ResolveStyle(PresentationTextRole.ComposerBackgroundPaneRole);
            WorkspaceLayout.DrawFrame(surface, background);
            if (surface is not BufferSurface buffer)
            {
                return;
            }

            var view = buffer.CreateView(WorkspaceLayout.ComposerContent(surface.Size));
            var promptWidth = PromptWidth(view.Size.Width);
            _owner._composer.FirstRowOffset = promptWidth;
            _owner._composer.Style = _owner.ResolvePaneStyle(PresentationTextRole.Default, PresentationTextRole.ComposerBackgroundPaneRole);
            _owner._composer.Render(view);
            _prompt.Draw(view, 0, 0, _owner._prompt, _owner.ResolvePaneStyle(PresentationTextRole.ComposerPrompt, PresentationTextRole.ComposerBackgroundPaneRole));
            if (!_owner.CanEdit)
            {
                _prompt.Draw(surface, 2, 0, " Read-only — return to MAIN ", background);
            }
        }

        private int PromptWidth(int availableWidth)
        {
            return Math.Min(UnicodeWidth.GetWidth(_owner._prompt), Math.Max(0, availableWidth - 1));
        }
    }
}
