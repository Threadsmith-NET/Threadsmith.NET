namespace Threadsmith.Tui.TuiKit;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Telemetry;
using TUIKit.Modals;

/// <summary>Owns startup, command-help, and immediate-toggle modal lifetimes on the existing terminal runtime.</summary>
internal sealed partial class TuiKitSurface : IStartupProgressSurface, IInteractionActionToggleSurface, IInteractionHelpSurface, IContextUsageSurface, IRepositoryMemoryListSurface, IToolStatisticsSurface
{
    private readonly List<SemanticStartupPhaseSnapshot> _startupPhases = [];
    private SemanticStartupProgress _startupTiming = new();
    private Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? _processStartupProgress;
    private bool _startupConfigured;
    private bool _startupServicesAttached;
    private IReadOnlyList<string> _startupDetails = [];
    private long _startupDwellStart;
    private StartupTips? _startupTips;
    private StartupModal? _startupModal;
    private Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? _startupProgress;

    /// <inheritdoc />
    public Task ShowRepositoryMemoriesAsync(RepositoryMemoryListSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        return ShowRetainedModalAsync(
            new RepositoryMemoryModal(snapshot, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture(), () => _app.MouseCaptureEnabled),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ManageRepositoryMemoriesAsync(
        RepositoryMemoryListSnapshot snapshot,
        Func<RepositoryMemoryListItem, string?, CancellationToken, Task<RepositoryMemoryListSnapshot>> change,
        Func<RepositoryMemoryType, RepositoryMemoryOwnership, string, CancellationToken, Task<RepositoryMemoryListSnapshot>> add,
        CancellationToken cancellationToken = default)
    {
        var modal = new RepositoryMemoryModal(snapshot, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture(), () => _app.MouseCaptureEnabled) { CanManage = true };
        return RunRetainedActionModalAsync(
            modal,
            modal.Changes,
            async (request, token) =>
        {
            RepositoryMemoryListSnapshot? refreshed = null;
            string? error = null;
            try
            {
                if (request.Item is { } item)
                {
                    refreshed = request.Delete
                        ? await change(item, null, token)
                        : await EditRepositoryMemoryAsync(item.Text, "Edit memory", (text, ct) => change(item, text, ct), token);
                }
                else
                {
                    var type = request.Tab == 0 ? RepositoryMemoryType.StandingPreference : RepositoryMemoryType.Situational;
                    var ownership = request.Tab == 2 ? RepositoryMemoryOwnership.Intelligence : RepositoryMemoryOwnership.Ordinary;
                    var category = request.Tab == 0 ? "Preferences" : request.Tab == 1 ? "Situational" : "Intelligence";
                    refreshed = await EditRepositoryMemoryAsync(string.Empty, "Add memory / " + category, (text, ct) => add(type, ownership, text, ct), token);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception.Message;
            }

            await EnqueueAsync(() => modal.CompleteChange(refreshed, error), token);
            return false;
        },
            nested: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ShowToolStatisticsAsync(
        Func<CancellationToken, Task<ToolStatisticsSnapshot>> load,
        Func<CancellationToken, Task<ToolStatisticsSnapshot>> reset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(reset);
        var modal = new ToolStatisticsModal(ResolveStyle, _interrupt, () => _app.ToggleMouseCapture(), () => _app.MouseCaptureEnabled);
        return RunRetainedActionModalAsync(
            modal,
            modal.Requests,
            async (resetRequested, token) =>
        {
            ToolStatisticsSnapshot? snapshot = null;
            string? error = null;
            try
            {
                snapshot = resetRequested ? await reset(token) : await load(token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error = exception.Message;
            }

            await EnqueueAsync(
                () =>
            {
                if (!token.IsCancellationRequested)
                {
                    modal.CompleteRequest(snapshot, error);
                }
            },
                token);
            return false;
        },
            nested: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ShowContextUsageAsync(ContextUsageSnapshot? snapshot, CancellationToken cancellationToken = default)
    {
        return ShowRetainedModalAsync(
            new ContextUsageModal(
                snapshot,
                ResolveStyle,
                _interrupt,
                () => _app.ToggleMouseCapture(),
                () => _app.MouseCaptureEnabled && !_startupBlocked && ModalFrame.Fits(_backend.Size)),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task ShowCommandHelpAsync(IReadOnlyList<InteractiveCommandDescriptor> commands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        return ShowRetainedModalAsync(new CommandHelpModal(commands, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture()), cancellationToken);
    }

    /// <inheritdoc />
    public Task SetStartupDetailsAsync(IReadOnlyList<string> details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var snapshot = details.ToArray();
        return EnqueueAsync(
            () =>
        {
            _startupDetails = snapshot;
            _startupModal?.Details = snapshot;
        },
            cancellationToken);
    }

    /// <inheritdoc />
    [SuppressMessage("Usage", "VSTHRD003", Justification = "The host owns and independently observes the operation represented by this modal.")]
    public Task ShowStartupAsync(string logo, string label, Task operation, CancellationToken cancellationToken = default)
    {
        return ShowStartupAsync(logo, label, operation.WaitAsync, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetStartupProgressAsync(Func<IReadOnlyList<SemanticStartupPhaseSnapshot>>? snapshots, CancellationToken cancellationToken = default)
    {
        return EnqueueAsync(() => _startupProgress = snapshots, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ShowStartupAsync(string logo, string label, Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        using var tracked = _startupTiming.Measure($"outer.{_startupPhases.Count}", label, cancellationToken);
        var modal = new StartupModal(logo, label, _startupPhases.ToArray(), _interrupt, ResolveStyle, _startupDetails) { Operation = tracked };
        await EnqueueAsync(
            () =>
        {
            _startupBlocked = true;
            _startupTips ??= new StartupTips(TipsCatalog.All);
            modal.Tips = _startupTips;
            modal.Progress = () => _processStartupProgress?.Invoke() ?? [];
            modal.SemanticProgress = () => _startupProgress?.Invoke() ?? [];
            modal.TotalElapsed = () => Stopwatch.GetElapsedTime(_startupDwellStart);
            _inputEpoch++;
            ClosePalette();
            DismissStartupModal();
            _startupModal = modal;
            _ = _app.ShowAsync<string>(modal);

            // Paint on the terminal owner before the host starts synchronous loading work.
            _app.RenderOnce();
        },
            cancellationToken);
        _ = Interlocked.CompareExchange(ref _startupDwellStart, Stopwatch.GetTimestamp(), 0);
        var outcome = "Completed";
        try
        {
            await operation(cancellationToken);
            tracked?.Complete();
        }
        catch (OperationCanceledException)
        {
            outcome = "Cancelled";
            throw;
        }
        catch
        {
            outcome = "Failed";
            throw;
        }
        finally
        {
            tracked.Dispose();
            try
            {
                if (!_stop.IsCancellationRequested && outcome == "Completed")
                {
                    await EnqueueAsync(
                        () =>
                    {
                        modal.Complete();
                        _app.RenderOnce();
                    },
                        _stop.Token);
                    var remaining = TimeSpan.FromMilliseconds(250)
                        - Stopwatch.GetElapsedTime(Volatile.Read(ref _startupDwellStart));
                    if (remaining > TimeSpan.Zero)
                    {
                        using var dwellCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
                        await Task.Delay(remaining, dwellCancellation.Token);
                    }
                }
            }
            finally
            {
                if (!_stop.IsCancellationRequested)
                {
                    await EnqueueAsync(
                        () =>
                    {
                        var snapshot = tracked.Snapshot;
                        var completed = StartupModal.FormatRow(snapshot.Elapsed, label, Enum.Parse<SemanticStartupPhaseState>(outcome), string.Empty, int.MaxValue);
                        _startupPhases.Add(snapshot);

                        if (outcome == "Completed" && label.StartsWith("Loading ", StringComparison.Ordinal) && modal.SplinesCompleted)
                        {
                            _startupPhases.Add(new(default, SemanticStartupPhaseState.Completed, modal.SplinesElapsed) { Label = "Reticulating Splines" });
                        }

                        if (outcome != "Completed")
                        {
                            Agents.Main.Transcript.Present(new PresentationBatch([new PresentationTextItem([new(completed + "\n", PresentationTextRole.Error)])]));
                        }

                        // Keep successful work visible until the next stage or composer takes over.
                        if (outcome != "Completed" || cancellationToken.IsCancellationRequested)
                        {
                            DismissStartupModal();
                        }

                        _inputEpoch++;
                    },
                        _stop.Token);
                }
            }
        }
    }

    /// <inheritdoc />
    public Task SelectTogglesAsync(InteractionToggleRequest request, Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change, CancellationToken cancellationToken = default)
    {
        return SelectTogglesCoreAsync(request, change, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task SelectActionTogglesAsync(
        InteractionToggleRequest request,
        Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
        Func<string, string, CancellationToken, Task<InteractionToggleResult>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return SelectTogglesCoreAsync(request, change, action, cancellationToken);
    }

    /// <summary>Emits the first modal frame before configuration or services start.</summary>
    internal Task BeginProcessStartupAsync(SemanticStartupProgress progress, Func<IReadOnlyList<SemanticStartupPhaseSnapshot>> snapshots, CancellationToken cancellationToken)
    {
        return EnqueueAsync(
            () =>
        {
            _startupTiming = progress;
            _processStartupProgress = snapshots;
            _startupBlocked = true;
            _startupTips = new StartupTips(TipsCatalog.All);
            _startupDwellStart = Stopwatch.GetTimestamp();
            _startupModal = new StartupModal(StartupBanner.Text, null, [], _interrupt, ResolveStyle)
            {
                Tips = _startupTips,
                Progress = snapshots,
                TotalElapsed = () => Stopwatch.GetElapsedTime(_startupDwellStart),
            };
            _ = _app.ShowAsync<string>(_startupModal);
            _app.RenderOnce();
        },
            cancellationToken);
    }

    /// <summary>Applies configuration once, before session state or editable input exists.</summary>
    internal Task ConfigureStartupAsync(IConfiguration configuration, CancellationToken cancellationToken)
    {
        var (catalog, defaultId) = TuiThemeConfigurationLoader.Load(configuration);
        var themes = new SessionThemePreferences(catalog, defaultId);
        var display = TuiDisplayOptions.Load(configuration);
        return EnqueueAsync(
            () =>
        {
            if (_startupConfigured)
            {
                throw new InvalidOperationException("Startup display configuration is already attached.");
            }

            _startupConfigured = true;
            _limits = display.Limits;
            _theme = themes.ActiveTheme;
            _styles = new TuiKitStyles(_theme, _suppressStyles);
            InitializeWorkspace();
        },
            cancellationToken);
    }

    /// <summary>Attaches storage services once before the composer is available.</summary>
    internal Task AttachStartupServicesAsync(IImageArtifactStore? images, IToolStatisticsHealthSource? statistics, CancellationToken cancellationToken)
    {
        return EnqueueAsync(
            () =>
        {
            if (_startupServicesAttached)
            {
                throw new InvalidOperationException("Startup services are already attached.");
            }

            _startupServicesAttached = true;
            _imageArtifacts = images;
            _toolStatisticsHealth = statistics;
        },
            cancellationToken);
    }

    /// <summary>Routes an early diagnostic through the terminal owner.</summary>
    internal Task ReportStartupDiagnosticAsync(string message, CancellationToken cancellationToken)
    {
        var safe = TranscriptView.Safe(new SecretOutputSanitizer().Sanitize(message));
        safe = safe.Length > 512 ? safe[..512] : safe;
        return SetStartupDetailsAsync(_startupDetails.TakeLast(7).Append(safe).ToArray(), cancellationToken);
    }

    /// <summary>Keeps a fatal outcome visible until Enter, cancellation, or terminal loss.</summary>
    internal async Task<string> AcknowledgeStartupFailureAsync(string message, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        if (!string.IsNullOrWhiteSpace(message))
        {
            await ReportStartupDiagnosticAsync(message, lifetime.Token);
        }

        var failureDetail = string.Empty;
        var acknowledged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failedElapsed = Stopwatch.GetElapsedTime(_startupDwellStart);
        await EnqueueAsync(
            () =>
        {
            failureDetail = _startupDetails.Count == 0 ? "Startup failed." : _startupDetails[^1];
            DismissStartupModal();
            _startupModal = new StartupModal(StartupBanner.Text, "Startup", _startupPhases.ToArray(), _interrupt, ResolveStyle, _startupDetails)
            {
                Progress = _processStartupProgress,
                Acknowledge = () => acknowledged.TrySetResult(),
                TotalElapsed = () => failedElapsed,
            };
            _startupModal.Finish(SemanticStartupPhaseState.Failed, failedElapsed);
            _ = _app.ShowAsync<string>(_startupModal);
            _app.RenderOnce();
        },
            lifetime.Token);
        await acknowledged.Task.WaitAsync(lifetime.Token);
        return failureDetail;
    }

    private void DismissStartupModal()
    {
        if (_startupModal is null)
        {
            return;
        }

        _startupModal.RequestClose(null);
        _app.Modals.RemoveClosed();
        _startupModal = null;
    }

    private async Task<RepositoryMemoryListSnapshot?> EditRepositoryMemoryAsync(
        string initialText,
        string title,
        Func<string, CancellationToken, Task<RepositoryMemoryListSnapshot>> save,
        CancellationToken cancellationToken)
    {
        var modal = new RepositoryMemoryEditModal(initialText, ResolveStyle, _limits, () => _app.MouseCaptureEnabled, () => _app.ToggleMouseCapture()) { CopyRequested = Copy, Title = title };
        modal.PasteRequested = () => RequestPaste(modal, () => modal.Text);
        RepositoryMemoryListSnapshot? refreshed = null;
        await RunRetainedActionModalAsync(
            modal,
            modal.Saves,
            async (text, token) =>
        {
            try
            {
                refreshed = await save(text, token);
                return true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await EnqueueAsync(() => modal.SaveFailed(exception.Message), token);
                return false;
            }
        },
            nested: true,
            cancellationToken);
        return refreshed;
    }

    [SuppressMessage("Usage", "VSTHRD003", Justification = "Modal completion is owned by the dedicated terminal loop.")]
    private async Task RunRetainedActionModalAsync<T>(
        Modal modal,
        ChannelReader<T> requests,
        Func<T, CancellationToken, Task<bool>> apply,
        bool nested,
        CancellationToken cancellationToken)
    {
        if (!nested)
        {
            await _interactionModalGate.WaitAsync(cancellationToken);
        }

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Nested editors retain their parent's input lease through cancellation cleanup.
        var completion = ShowRetainedModalCoreAsync(modal, lifetime.Token);
        try
        {
            while (!completion.IsCompleted)
            {
                var next = requests.ReadAsync(lifetime.Token).AsTask();
                if (await Task.WhenAny(next, completion) == completion)
                {
                    await lifetime.CancelAsync();
                    try
                    {
                        await next;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }

                    break;
                }

                var action = apply(await next, lifetime.Token);
                if (await Task.WhenAny(action, completion) == completion)
                {
                    await lifetime.CancelAsync();
                    try
                    {
                        await action;
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }

                    break;
                }

                if (await action)
                {
                    await EnqueueAsync(
                        () =>
                    {
                        modal.RequestClose(null);
                        _app.Modals.RemoveClosed();
                    },
                        cancellationToken);
                    break;
                }
            }

            await completion;
        }
        finally
        {
            try
            {
                await lifetime.CancelAsync();
                try
                {
                    await completion;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
            }
            finally
            {
                if (!nested)
                {
                    _interactionModalGate.Release();
                }
            }
        }
    }

    private async Task ShowRetainedModalAsync(Modal modal, CancellationToken cancellationToken)
    {
        await _interactionModalGate.WaitAsync(cancellationToken);
        try
        {
            await ShowRetainedModalCoreAsync(modal, cancellationToken);
        }
        finally
        {
            _interactionModalGate.Release();
        }
    }

    private async Task ShowRetainedModalCoreAsync(Modal modal, CancellationToken cancellationToken)
    {
        Task<string?>? completion = null;
        await EnqueueAsync(
            () =>
        {
            ClosePalette();
            completion = _app.ShowAsync<string>(modal);
        },
            cancellationToken);
        try
        {
            _ = await (completion ?? throw new InvalidOperationException("Retained modal did not open.")).WaitAsync(cancellationToken);
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(
                    () =>
                {
                    modal.RequestClose(null);
                    _app.Modals.RemoveClosed();
                },
                    _stop.Token);
            }
        }
    }

    [SuppressMessage("Usage", "VSTHRD003", Justification = "Modal completion and UI acknowledgements are owned by the dedicated terminal loop.")]
    private async Task SelectTogglesCoreAsync(
        InteractionToggleRequest request,
        Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
        Func<string, string, CancellationToken, Task<InteractionToggleResult>>? action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(change);
        if (request.Options.Count == 0 || request.Options.Count > _limits.MaximumToggleOptions
            || request.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
        {
            throw new ArgumentException($"Toggle catalogs require 1–{_limits.MaximumToggleOptions:N0} distinct stable IDs.", nameof(request));
        }

        var modal = new ToggleModal(request, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture(), _limits) { CopyRequested = Copy, SupportsActions = action is not null };
        Task<string?>? closed = null;
        await EnqueueAsync(() => closed = _app.ShowAsync<string>(modal), cancellationToken);
        var completion = closed ?? throw new InvalidOperationException("Toggle popup did not open.");
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (!completion.IsCompleted)
            {
                var next = modal.Changes.ReadAsync(wait.Token).AsTask();
                if (await Task.WhenAny(next, completion) == completion)
                {
                    await wait.CancelAsync();
                    try
                    {
                        await next;
                    }
                    catch (OperationCanceledException) when (wait.IsCancellationRequested)
                    {
                    }

                    break;
                }

                var requested = await next;
                if (requested.Actions && action is not null)
                {
                    var choices = modal.ActionChoices(requested.Id);
                    if (choices.Count > 0)
                    {
                        var selection = await SelectAsync(new InteractionSelectionRequest(modal.Label(requested.Id), choices), cancellationToken);
                        if (!selection.IsCancelled && selection.SelectedOptionId is { } actionId
                            && choices.Any(item => item.Id == actionId))
                        {
                            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var label = choices.Single(item => item.Id == actionId).Label;
                            await EnqueueAsync(
                                () =>
                            {
                                if (completion.IsCompleted)
                                {
                                    operation.Cancel();
                                }
                                else
                                {
                                    // Admission and the Esc cancellation hook must share one UI turn.
                                    modal.BeginAction(label, operation.Cancel);
                                }
                            },
                                cancellationToken);
                            (string Id, InteractionToggleResult Result)? pending = null;
                            try
                            {
                                foreach (var option in modal.ActionMembers(requested.Id))
                                {
                                    if (operation.IsCancellationRequested)
                                    {
                                        break;
                                    }

                                    if (pending is { } previous)
                                    {
                                        await EnqueueAsync(() => modal.Reconcile(previous.Id, previous.Result), cancellationToken);
                                        pending = null;
                                    }

                                    var result = await action(option.Id, actionId, operation.Token);
                                    pending = (option.Id, result);
                                }
                            }
                            finally
                            {
                                if (!_stop.IsCancellationRequested)
                                {
                                    await EnqueueAsync(
                                        () =>
                                    {
                                        if (pending is { } final)
                                        {
                                            modal.Reconcile(final.Id, final.Result);
                                        }

                                        modal.CompleteChange();
                                    },
                                        _stop.Token);
                                }
                            }

                            continue;
                        }
                    }

                    await EnqueueAsync(modal.CompleteChange, cancellationToken);
                    continue;
                }

                await ApplyToggleChangeAsync(modal, completion, requested.Id, requested.Enabled, change, cancellationToken);
            }
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(() => modal.RequestClose(null), _stop.Token);
            }
        }
    }

    private async Task ApplyToggleChangeAsync(
        ToggleModal modal,
        Task completion,
        string id,
        bool enabled,
        Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
        CancellationToken cancellationToken)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IReadOnlyList<InteractionToggleOption> members = [];
        var verb = enabled ? "Enabling" : "Disabling";
        await EnqueueAsync(
            () =>
        {
            if (completion.IsCompleted)
            {
                operation.Cancel();
            }
            else
            {
                // Freeze the filtered targets and install cancellation before admitting host work.
                members = modal.Members(id);
                modal.BeginToggle($"{verb} 0/{members.Count}", operation.Cancel);
            }
        },
            cancellationToken);
        var processed = 0;
        var applied = 0;
        (string Id, InteractionToggleResult Result)? pending = null;
        try
        {
            foreach (var option in members)
            {
                operation.Token.ThrowIfCancellationRequested();
                if (pending is { } previous)
                {
                    await EnqueueAsync(
                        () =>
                    {
                        modal.Reconcile(previous.Id, previous.Result);
                        modal.BeginToggle($"{verb} {processed}/{members.Count}", operation.Cancel);
                    },
                        cancellationToken);
                    pending = null;
                }

                operation.Token.ThrowIfCancellationRequested();

                // Finish an admitted mutation and its authoritative publication before honoring local Esc.
                var result = await change(option.Id, enabled, cancellationToken);
                pending = (option.Id, result);
                processed++;
                if (result.Enabled == enabled)
                {
                    applied++;
                }
            }
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Esc cancels this batch; completed acknowledgements remain visible in the open tree.
        }
        finally
        {
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(
                    () =>
                {
                    if (pending is { } final)
                    {
                        modal.Reconcile(final.Id, final.Result);
                    }

                    modal.CompleteToggle(enabled, members.Count, processed, applied, operation.IsCancellationRequested);
                },
                    _stop.Token);
            }
        }
    }
}
