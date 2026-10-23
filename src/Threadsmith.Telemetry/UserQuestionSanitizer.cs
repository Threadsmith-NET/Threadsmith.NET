namespace Threadsmith.Telemetry;

using Threadsmith.Core;

/// <summary>Applies the host text policy before clarification content is retained or replayed.</summary>
public static class UserQuestionSanitizer
{
    /// <summary>Copies and sanitizes display content without changing correlation identities.</summary>
    public static UserQuestionRequest Sanitize(UserQuestionRequest request, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sanitizer);
        UserQuestionValidation.ValidateQuestions(request.Questions);
        var safe = CopyRequest(request, sanitizer, out var changed);
        if (changed)
        {
            UserQuestionValidation.ValidateQuestions(safe.Questions);
        }

        return safe;
    }

    /// <summary>Sanitizes submitted answers before terminal selection, publication or retention.</summary>
    public static UserQuestionResult Sanitize(UserQuestionRequest request, UserQuestionResult result, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ValidateResult(request, result);
        var safe = CopyResult(result, sanitizer, out var changed);
        if (changed)
        {
            ValidateResult(request, safe);
        }

        return safe;
    }

    /// <summary>Validates and sanitizes a replayed lifecycle once, rechecking bounds only when text changes.</summary>
    public static UserQuestionStateChanged Sanitize(UserQuestionStateChanged state, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(state.Request);
        ArgumentNullException.ThrowIfNull(state.Result);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ValidateResult(state.Request, state.Result);
        var request = CopyRequest(state.Request, sanitizer, out var requestChanged);
        var result = CopyResult(state.Result, sanitizer, out var resultChanged);
        if (requestChanged || resultChanged)
        {
            ValidateResult(request, result);
        }

        return state with { Request = request, Result = result };
    }

    private static void ValidateResult(UserQuestionRequest request, UserQuestionResult result)
    {
        ArgumentNullException.ThrowIfNull(result.Answers);
        if (!Enum.IsDefined(result.Status) || (result.Status != UserQuestionStatus.Answered && result.Answers.Count != 0))
        {
            throw new ArgumentException("Invalid clarification outcome.", nameof(result));
        }

        // Answer validation includes request validation, including both serialized byte bounds.
        if (result.Status == UserQuestionStatus.Answered)
        {
            UserQuestionValidation.ValidateAnswers(request.Questions, result.Answers);
        }
        else
        {
            UserQuestionValidation.ValidateQuestions(request.Questions);
        }
    }

    private static UserQuestionRequest CopyRequest(UserQuestionRequest request, IOutputSanitizer sanitizer, out bool changed)
    {
        var textChanged = false;
        var questions = request.Questions.Select(question =>
        {
            ValidateIdentity(question.Id, sanitizer);
            var prompt = sanitizer.Sanitize(question.Prompt);
            textChanged |= prompt != question.Prompt;
            var options = question.Options.Select(option =>
            {
                ValidateIdentity(option.Id, sanitizer);
                var label = sanitizer.Sanitize(option.Label);
                textChanged |= label != option.Label;
                return option with { Label = label };
            }).ToArray();
            return question with { Prompt = prompt, Options = options };
        }).ToArray();
        changed = textChanged;
        return request with { Questions = questions };
    }

    private static UserQuestionResult CopyResult(UserQuestionResult result, IOutputSanitizer sanitizer, out bool changed)
    {
        var textChanged = false;
        var answers = result.Answers.Select(answer =>
        {
            var text = answer.Text is null ? null : sanitizer.Sanitize(answer.Text);
            textChanged |= text != answer.Text;
            return answer with { Text = text };
        }).ToArray();
        changed = textChanged;
        return result with { Answers = answers };
    }

    private static void ValidateIdentity(string identity, IOutputSanitizer sanitizer)
    {
        if (sanitizer.Sanitize(identity) != identity)
        {
            throw new ArgumentException("Clarification identifiers cannot contain sensitive content.");
        }
    }
}
