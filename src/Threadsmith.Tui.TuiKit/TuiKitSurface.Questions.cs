namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using TUIKit.Input;

#pragma warning disable SA1601 // The canonical partial-type documentation is owned by TuiKitSurface.cs.
internal sealed partial class TuiKitSurface : IUserQuestionSurface
{
#pragma warning restore SA1601
    private readonly SemaphoreSlim _interactionModalGate = new(1, 1);
    private QuestionDraft? _questionDraft;
    private QuestionModal? _questionModal;
    private TaskCompletionSource<UserQuestionResult>? _questionCompletion;

    /// <summary>Gets or sets the trusted host bell preference; never sourced from question content.</summary>
    internal bool QuestionBellEnabled { get; set; }

    /// <inheritdoc />
    public async Task<UserQuestionResult> AskAsync(UserQuestionRequest request, CancellationToken cancellationToken = default)
    {
        UserQuestionValidation.ValidateQuestions(request.Questions);
        var completion = new TaskCompletionSource<UserQuestionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _interactionModalGate.WaitAsync(cancellationToken);
        try
        {
            await _utilityModal.WaitAsync(cancellationToken);
            await EnqueueAsync(
                () =>
            {
                _app.Modals.RemoveClosed();
                if (_questionDraft is not null || _app.Modals.IsActive)
                {
                    throw new InvalidOperationException("Question presentation requires the shared modal input owner.");
                }

                _questionDraft = new QuestionDraft(request, _limits);
                _questionCompletion = completion;
                ShowQuestion();
                if (QuestionBellEnabled && _backend.IsInteractive)
                {
                    try
                    {
                        _backend.Write("\a");
                        _backend.Flush();
                    }
                    catch (Exception exception) when (exception is IOException or InvalidOperationException or NotSupportedException)
                    {
                        _notice = "Attention bell unavailable; the question remains pending.";
                    }
                }
            },
                cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            try
            {
                if (!_stop.IsCancellationRequested)
                {
                    await EnqueueAsync(
                        () =>
                    {
                        if (ReferenceEquals(_questionCompletion, completion))
                        {
                            _questionModal?.RequestClose(null);
                            _questionDraft = null;
                            _questionModal = null;
                            _questionCompletion = null;
                        }
                    },
                        _stop.Token);
                }
            }
            finally
            {
                _interactionModalGate.Release();
            }
        }
    }

    private void ShowQuestion()
    {
        if (_questionDraft is null || _app.Modals.IsActive)
        {
            return;
        }

        var modal = new QuestionModal(_questionDraft, _limits)
        {
            ResolveStyle = ResolveStyle,
            MuteRequested = () => QuestionBellEnabled = false,
            CancelRunRequested = () => Volatile.Read(ref _activeInput)?.Cancel(),
        };
        modal.PasteRequested = () => RequestPaste(modal, () => modal.PasteIdentity);
        _questionModal = modal;
        _utilityModal = ObserveQuestionAsync(modal, _questionCompletion);
    }

    private async Task ObserveQuestionAsync(QuestionModal modal, TaskCompletionSource<UserQuestionResult>? completion)
    {
        var answer = await _app.ShowAsync<UserQuestionResult>(modal);
        completion?.TrySetResult(answer ?? new UserQuestionResult(UserQuestionStatus.Cancelled, []));
    }

    private bool HandleQuestionKey(KeyEvent key)
    {
        if (_questionDraft is null)
        {
            return false;
        }

        if (key.Code == KeyCode.F10)
        {
            QuestionBellEnabled = false;
            return true;
        }

        _app.Modals.RemoveClosed();

        if (_app.Modals.IsActive)
        {
            return _questionModal is { } modal && ReferenceEquals(_app.Modals.Top, modal) && modal.HandleKey(key);
        }

        // Consume input only while the completed modal is leaving the shared input owner.
        return true;
    }
}
