namespace Threadsmith.Tui.TuiKit;

using Threadsmith.Core;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Presentation;
using TUIKit;
using TUIKit.Content;
using TUIKit.Input;
using TUIKit.Modals;

/// <summary>Draft owned by one request across question navigation.</summary>
internal sealed class QuestionDraft
{
    /// <summary>Initializes a new instance of the <see cref="QuestionDraft"/> class.</summary>
    internal QuestionDraft(UserQuestionRequest request, TuiResourceLimits limits)
    {
        Request = request;
        Editors = [.. request.Questions.Select(_ => new TuiKitComposer(limits))];
        SelectedOptions = new string?[request.Questions.Count];
        OtherSelected = [.. request.Questions.Select(question => question.Options.Count == 0)];
    }

    /// <summary>Gets the immutable request bound to this draft.</summary>
    internal UserQuestionRequest Request { get; }

    /// <summary>Gets independently retained text editors.</summary>
    internal TuiKitComposer[] Editors { get; }

    /// <summary>Gets explicitly selected option identities; initial focus selects nothing.</summary>
    internal string?[] SelectedOptions { get; }

    /// <summary>Gets whether each question uses its host-owned free-text editor.</summary>
    internal bool[] OtherSelected { get; }

    /// <summary>Gets or sets the visible question page.</summary>
    internal int QuestionIndex { get; set; }
}

/// <summary>Explicit clarification input using the existing modal frame, composer, and safe transcript.</summary>
internal sealed class QuestionModal : Modal
{
    private readonly QuestionDraft _draft;
    private readonly TuiResourceLimits _limits;
    private readonly CachedTextRun _title = new();
    private readonly CachedTextRun _hints = new();
    private readonly CachedTextRun _submitHint = new();
    private readonly CachedTextRun _choice = new();
    private readonly CachedTextRun _actions = new();
    private readonly List<CachedTextRun> _optionRows = [];
    private TranscriptView _details;
    private bool _showDetails;
    private int _focus;
    private int _option;
    private bool _fits;
    private string? _error;

    /// <summary>Initializes a new instance of the <see cref="QuestionModal"/> class.</summary>
    internal QuestionModal(QuestionDraft draft, TuiResourceLimits limits)
    {
        _draft = draft;
        _limits = limits;
        _details = CreateDetails();
        FocusAnswer();
    }

    /// <summary>Gets or sets the existing frontend theme resolver.</summary>
    internal Func<PresentationTextRole, CellStyle> ResolveStyle { get; set; } = _ => CellStyle.Default;

    /// <summary>Gets or sets explicit clipboard dispatch through the UI owner.</summary>
    internal Action? PasteRequested { get; set; }

    /// <summary>Gets or sets the host-owned mute action while the modal owns input.</summary>
    internal Action? MuteRequested { get; set; }

    /// <summary>Gets or sets cancellation of the owning active run.</summary>
    internal Action? CancelRunRequested { get; set; }

    /// <summary>Gets the focus and edit revision fencing asynchronous clipboard completion.</summary>
    internal string PasteIdentity => $"{_draft.QuestionIndex}:{_focus}:{_draft.Editors[_draft.QuestionIndex].Buffer.Revision}";

    /// <inheritdoc />
    public override bool HandleKey(KeyEvent key)
    {
        key = TuiKitInput.Normalize(key);
        if (key.Code == KeyCode.Character && key.Rune == 'c' && key.Modifiers == KeyModifiers.Ctrl)
        {
            CancelRunRequested?.Invoke();
            return true;
        }

        if (key.Code is KeyCode.F5 or KeyCode.Escape)
        {
            Close(new UserQuestionResult(UserQuestionStatus.Cancelled, []));
            return true;
        }

        if (key.Code == KeyCode.F10)
        {
            MuteRequested?.Invoke();
            return true;
        }

        if (!_fits)
        {
            return true;
        }

        _error = null;

        if (key.Code == KeyCode.F2)
        {
            _showDetails = !_showDetails;
            return true;
        }

        var question = _draft.Request.Questions[_draft.QuestionIndex];
        if (key.Code == KeyCode.Enter)
        {
            _showDetails = false;
            if (_focus == 6)
            {
                Close(new UserQuestionResult(UserQuestionStatus.Cancelled, []));
            }
            else
            {
                if (_focus == 1)
                {
                    SelectOption();
                }

                Submit();
            }

            return true;
        }

        if (_showDetails)
        {
            _details.HandleKey(key);
            return true;
        }

        if (key.Code == KeyCode.Tab)
        {
            do
            {
                _focus = (_focus + (key.Modifiers.HasFlag(KeyModifiers.Shift) ? 6 : 1)) % 7;
            }
            while (_focus == 0 || (_focus == 1 && _draft.Request.Questions[_draft.QuestionIndex].Options.Count == 0)
                || (_focus == 2 && !_draft.OtherSelected[_draft.QuestionIndex])
                || (_focus is 3 or 4 && _draft.Request.Questions.Count == 1));
            return true;
        }

        if (key.Code is KeyCode.PageUp or KeyCode.PageDown)
        {
            ChangeQuestion(key.Code == KeyCode.PageUp ? -1 : 1);
            return true;
        }

        if (_focus == 1 && question.Options.Count > 0)
        {
            if (key.Code is KeyCode.Up or KeyCode.Down)
            {
                _option = Math.Clamp(_option + (key.Code == KeyCode.Up ? -1 : 1), 0, question.Options.Count);
            }
            else if (key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.None && key.Rune == ' ')
            {
                SelectOption();
            }
            else if (key.Code == KeyCode.Character && key.Modifiers == KeyModifiers.None
                && key.Rune >= '1' && key.Rune <= '1' + question.Options.Count)
            {
                _option = key.Rune - '1';
                SelectOption();
            }
        }
        else if (_focus == 2)
        {
            if ((key.Code == KeyCode.Character && key.Rune == 'v' && key.Modifiers == KeyModifiers.Ctrl)
                || (key.Code == KeyCode.Insert && key.Modifiers == KeyModifiers.Shift))
            {
                PasteRequested?.Invoke();
            }
            else
            {
                var editor = _draft.Editors[_draft.QuestionIndex];
                var previous = editor.Text;
                editor.HandleKey(key);
                if (editor.Text.Length > 8000)
                {
                    editor.Text = previous;
                    _error = "Answers are limited to 8,000 characters.";
                }
                else if (editor.Text != previous)
                {
                    _draft.SelectedOptions[_draft.QuestionIndex] = null;
                }
            }
        }
        else if (key.Code == KeyCode.Character && key.Rune == ' ' && key.Modifiers == KeyModifiers.None)
        {
            if (_focus is 3 or 4)
            {
                ChangeQuestion(_focus == 3 ? -1 : 1);
            }
            else if (_focus == 5)
            {
                Submit();
            }
            else if (_focus == 6)
            {
                Close(new UserQuestionResult(UserQuestionStatus.Cancelled, []));
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool HandlePaste(string text)
    {
        if (_fits && !_showDetails && _focus == 2)
        {
            var editor = _draft.Editors[_draft.QuestionIndex];
            if (text.Length <= 8000 - editor.Text.Length)
            {
                editor.Buffer.Insert(text);
                _draft.SelectedOptions[_draft.QuestionIndex] = null;
            }
            else
            {
                _error = "Paste exceeds the answer limit; draft preserved.";
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override void Render(ISurface surface)
    {
        _fits = ModalFrame.Fits(surface.Size) && surface.Size.Height >= 15;
        var question = _draft.Request.Questions[_draft.QuestionIndex];
        var contentWidth = Math.Max(1, Math.Min(90, surface.Size.Width - 4) - 4);
        var promptRows = TextWrapper.Wrap(StyledText.From(TranscriptView.Safe(question.Prompt)), contentWidth);
        var choiceCount = question.Options.Count == 0 ? 0 : question.Options.Count + 1;
        var showEditor = _draft.OtherSelected[_draft.QuestionIndex];
        var desiredHeight = Math.Min(4, promptRows.Count) + choiceCount + (showEditor ? 12 : 9);
        var frame = ModalFrame.Create(surface, ResolveStyle(PresentationTextRole.Default), preferredSize: new Size(90, _showDetails ? 26 : desiredHeight));
        if (frame is null)
        {
            return;
        }

        _title.Draw(frame, 0, 0, "The Assistant Has a Question", ResolveStyle(PresentationTextRole.SelectionPrompt));
        if (!_fits)
        {
            _hints.Draw(frame, 0, 1, "Enlarge to answer; Esc cancels.", ResolveStyle(PresentationTextRole.Status));
            return;
        }

        if (_showDetails)
        {
            _hints.Draw(frame, 0, 2, "Scroll; F2 back; Esc cancels", ResolveStyle(PresentationTextRole.Status));
            _details.ResolveStyle = ResolveStyle;
            _details.Render(frame.CreateView(new Rect(0, 3, frame.Size.Width, frame.Size.Height - 3)));
            return;
        }

        var promptHeight = Math.Min(promptRows.Count, Math.Max(1, Math.Min(4, frame.Size.Height - (showEditor ? 10 : 7))));
        for (var index = 0; index < promptHeight; index++)
        {
            DrawRow(frame, index, 2 + index, promptRows[index].ToPlainString(), PresentationTextRole.SelectionPrompt);
        }

        var optionTop = promptHeight + 2;
        var optionHeight = Math.Max(0, frame.Size.Height - optionTop - (showEditor ? 6 : 3));
        var top = Math.Max(0, _option - optionHeight + 1);
        for (var index = top; index < Math.Min(choiceCount, top + optionHeight); index++)
        {
            var focused = _focus == 1 && index == _option;
            var isOther = index == question.Options.Count;
            var selected = isOther ? showEditor : _draft.SelectedOptions[_draft.QuestionIndex] == question.Options[index].Id;
            DrawRow(
                frame,
                promptHeight + index,
                optionTop + index - top,
                $"{(focused ? ">" : " ")} [{(selected ? "x" : " ")}] {index + 1}. {(isOther ? "Other" : question.Options[index].Label)}",
                focused ? PresentationTextRole.SelectionHighlight : PresentationTextRole.Default);
        }

        if (showEditor)
        {
            var editorTop = optionTop + Math.Min(choiceCount, optionHeight);
            _choice.Draw(frame, 0, editorTop, $"{(_focus == 2 ? ">" : " ")} Your answer:", ResolveStyle(PresentationTextRole.Status));
            var editor = _draft.Editors[_draft.QuestionIndex];
            editor.Style = ResolveStyle(PresentationTextRole.Default);
            editor.OnFocusChanged(_focus == 2);
            editor.Render(frame.CreateView(new Rect(0, editorTop + 1, frame.Size.Width, Math.Max(1, frame.Size.Height - editorTop - 4))));
        }

        var pages = _draft.Request.Questions.Count > 1 ? $"{_draft.QuestionIndex + 1}/{_draft.Request.Questions.Count} {(_focus == 3 ? ">" : string.Empty)}[Prev] {(_focus == 4 ? ">" : string.Empty)}[Next] " : string.Empty;
        _actions.Draw(frame, 0, frame.Size.Height - 3, _error ?? $"{pages}{(_focus == 5 ? ">" : string.Empty)}[Submit] {(_focus == 6 ? ">" : string.Empty)}[Cancel]", ResolveStyle(PresentationTextRole.Status));
        var hint = _focus == 1 ? $"Arrows; 1-{choiceCount}/Space; Tab" : _draft.Request.Questions.Count > 1 ? "PgUp/PgDn question; Tab focus" : "Tab focus; paste supported";
        _hints.Draw(frame, 0, frame.Size.Height - 2, hint, ResolveStyle(PresentationTextRole.Status));
        _submitHint.Draw(frame, 0, frame.Size.Height - 1, "Enter submit; F2 more; Esc cancel", ResolveStyle(PresentationTextRole.Status));
    }

    private void DrawRow(ISurface surface, int index, int y, string text, PresentationTextRole role)
    {
        while (_optionRows.Count <= index)
        {
            _optionRows.Add(new CachedTextRun());
        }

        _optionRows[index].Draw(surface, 0, y, text, ResolveStyle(role));
    }

    private void FocusAnswer()
    {
        var question = _draft.Request.Questions[_draft.QuestionIndex];
        _option = Math.Max(0, question.Options.ToList().FindIndex(option => option.Id == _draft.SelectedOptions[_draft.QuestionIndex]));
        _focus = _draft.OtherSelected[_draft.QuestionIndex] ? 2 : 1;
        if (_draft.OtherSelected[_draft.QuestionIndex])
        {
            _option = question.Options.Count;
        }
    }

    private void SelectOption()
    {
        var question = _draft.Request.Questions[_draft.QuestionIndex];
        var other = _option == question.Options.Count;
        _draft.OtherSelected[_draft.QuestionIndex] = other;
        _draft.SelectedOptions[_draft.QuestionIndex] = other ? null : question.Options[_option].Id;
        if (other)
        {
            _focus = 2;
        }
        else
        {
            _draft.Editors[_draft.QuestionIndex].Buffer.Reset();
        }
    }

    private void ChangeQuestion(int delta)
    {
        _draft.QuestionIndex = Math.Clamp(_draft.QuestionIndex + delta, 0, _draft.Request.Questions.Count - 1);
        _details = CreateDetails();
        FocusAnswer();
    }

    private TranscriptView CreateDetails()
    {
        var details = new TranscriptView(_limits);
        var question = _draft.Request.Questions[_draft.QuestionIndex];
        var text = question.Prompt + "\n" + string.Join("\n", question.Options.Select((option, index) => $"{index + 1}. {option.Label}"));
        details.Present(new PresentationBatch([new PresentationTextItem([new(text, PresentationTextRole.Default)])]));
        details.HandleKey(KeyEvent.Special(KeyCode.Home));
        return details;
    }

    private void Submit()
    {
        var answers = _draft.Request.Questions.Select((question, index) => new UserQuestionAnswer(
            question.Id, _draft.SelectedOptions[index], _draft.SelectedOptions[index] is null ? _draft.Editors[index].Text : null)).ToArray();
        try
        {
            UserQuestionValidation.ValidateAnswers(_draft.Request.Questions, answers);
            Close(new UserQuestionResult(UserQuestionStatus.Answered, answers));
        }
        catch (ArgumentException)
        {
            var missing = Array.FindIndex(answers, answer => answer.OptionId is null && string.IsNullOrWhiteSpace(answer.Text));
            if (missing >= 0)
            {
                ChangeQuestion(missing - _draft.QuestionIndex);
                _error = $"Answer question {missing + 1}, then Enter.";
            }
            else
            {
                _error = "Answer too long; shorten it.";
            }
        }
    }
}
