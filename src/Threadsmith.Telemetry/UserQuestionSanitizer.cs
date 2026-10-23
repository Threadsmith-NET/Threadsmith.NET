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
        foreach (var question in request.Questions)
        {
            ValidateIdentity(question.Id, sanitizer);
            foreach (var option in question.Options)
            {
                ValidateIdentity(option.Id, sanitizer);
            }
        }

        var safe = request with
        {
            Questions = request.Questions.Select(question => question with
            {
                Prompt = sanitizer.Sanitize(question.Prompt),
                Options = question.Options.Select(option => option with { Label = sanitizer.Sanitize(option.Label) }).ToArray(),
            }).ToArray(),
        };
        UserQuestionValidation.ValidateQuestions(safe.Questions);
        return safe;
    }

    /// <summary>Sanitizes submitted answers before terminal selection, publication or retention.</summary>
    public static UserQuestionResult Sanitize(UserQuestionRequest request, UserQuestionResult result, IOutputSanitizer sanitizer)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(sanitizer);
        if (result.Status != UserQuestionStatus.Answered)
        {
            if (!Enum.IsDefined(result.Status) || result.Answers.Count != 0)
            {
                throw new ArgumentException("Invalid clarification outcome.", nameof(result));
            }

            return result with { Answers = [] };
        }

        UserQuestionValidation.ValidateAnswers(request.Questions, result.Answers);
        var safe = result with { Answers = result.Answers.Select(answer => answer with { Text = answer.Text is null ? null : sanitizer.Sanitize(answer.Text) }).ToArray() };
        UserQuestionValidation.ValidateAnswers(request.Questions, safe.Answers);
        return safe;
    }

    private static void ValidateIdentity(string identity, IOutputSanitizer sanitizer)
    {
        if (sanitizer.Sanitize(identity) != identity)
        {
            throw new ArgumentException("Clarification identifiers cannot contain sensitive content.");
        }
    }
}
