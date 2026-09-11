namespace Threadsmith.Tui.TuiKit;

using System.Diagnostics.CodeAnalysis;
using Threadsmith.Interaction.Commands;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;

/// <summary>Owns startup, command-help, and immediate-toggle modal lifetimes on the existing terminal runtime.</summary>
internal sealed partial class TuiKitSurface : IStartupProgressSurface, IInteractionActionToggleSurface, IInteractionHelpSurface
{
    private readonly List<string> _startupPhases = [];
    private IReadOnlyList<string> _startupDetails = [];

    /// <inheritdoc />
    public async Task ShowCommandHelpAsync(IReadOnlyList<InteractiveCommandDescriptor> commands, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var modal = new CommandHelpModal(commands, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture());
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
            _ = await (completion ?? throw new InvalidOperationException("Command help did not open.")).WaitAsync(cancellationToken);
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
    public Task SetStartupDetailsAsync(IReadOnlyList<string> details, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var snapshot = details.ToArray();
        return EnqueueAsync(() => _startupDetails = snapshot, cancellationToken);
    }

    /// <inheritdoc />
    [SuppressMessage("Usage", "VSTHRD003", Justification = "The host owns and independently observes the operation represented by this modal.")]
    public async Task ShowStartupAsync(string logo, string label, Task operation, CancellationToken cancellationToken = default)
    {
        var modal = new StartupModal(logo, label, _startupPhases.ToArray(), _interrupt, ResolveStyle, _startupDetails);
        await EnqueueAsync(
            () =>
        {
            _startupBlocked = true;
            _inputEpoch++;
            ClosePalette();
            _ = _app.ShowAsync<string>(modal);
        },
            cancellationToken);
        var outcome = "Completed";
        try
        {
            await operation.WaitAsync(cancellationToken);
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
            if (!_stop.IsCancellationRequested)
            {
                await EnqueueAsync(
                    () =>
                {
                    var completed = $"{label}: {outcome} · {modal.Elapsed.TotalSeconds:0.0}s";
                    _startupPhases.Add(completed);
                    if (outcome != "Completed")
                    {
                        _agents.Main.Transcript.Present(new PresentationBatch([new PresentationTextItem([new(completed + "\n", PresentationTextRole.Error)])]));
                    }

                    modal.RequestClose(null);
                    _inputEpoch++;
                },
                    _stop.Token);
            }
        }
    }

    /// <inheritdoc />
    public Task SelectTogglesAsync(InteractionToggleRequest request, Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change, CancellationToken cancellationToken = default)
        => SelectTogglesCoreAsync(request, change, null, cancellationToken);

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

    [SuppressMessage("Usage", "VSTHRD003", Justification = "Modal completion and UI acknowledgements are owned by the dedicated terminal loop.")]
    private async Task SelectTogglesCoreAsync(
        InteractionToggleRequest request,
        Func<string, bool, CancellationToken, Task<InteractionToggleResult>> change,
        Func<string, string, CancellationToken, Task<InteractionToggleResult>>? action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(change);
        if (request.Options.Count is 0 or > 2048 || request.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != request.Options.Count)
        {
            throw new ArgumentException("Toggle catalogs require 1–2048 distinct stable IDs.", nameof(request));
        }

        var modal = new ToggleModal(request, ResolveStyle, _interrupt, () => _app.ToggleMouseCapture()) { CopyRequested = Copy, SupportsActions = action is not null };
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
                    var option = modal.GetOption(requested.Id);
                    if (option is not null && option.Actions.Count > 0)
                    {
                        var selection = await SelectAsync(new InteractionSelectionRequest(option.Label, option.Actions), cancellationToken);
                        if (!selection.IsCancelled && selection.SelectedOptionId is { } actionId
                            && option.Actions.Any(item => item.Id == actionId))
                        {
                            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            var label = option.Actions.Single(item => item.Id == actionId).Label;
                            await EnqueueAsync(() => modal.BeginAction(label, operation.Cancel), cancellationToken);
                            InteractionToggleResult? result = null;
                            try
                            {
                                result = await action(option.Id, actionId, operation.Token);
                            }
                            finally
                            {
                                if (!_stop.IsCancellationRequested)
                                {
                                    await EnqueueAsync(
                                        () =>
                                    {
                                        if (result is not null)
                                        {
                                            modal.Reconcile(option.Id, result);
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

                foreach (var option in modal.Members(requested.Id))
                {
                    var result = await change(option.Id, requested.Enabled, cancellationToken);
                    await EnqueueAsync(() => modal.Reconcile(option.Id, result), cancellationToken);
                }

                await EnqueueAsync(modal.CompleteChange, cancellationToken);
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
}
