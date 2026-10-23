namespace Threadsmith.Core;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>A stable alternative in a clarification request; never an authorization grant.</summary>
public sealed record UserQuestionOption(string Id, string Label);

/// <summary>A bounded clarification with an optional single-choice answer.</summary>
public sealed record UserQuestion(string Id, string Prompt, IReadOnlyList<UserQuestionOption> Options);

/// <summary>An explicit local answer to one clarification.</summary>
public sealed record UserQuestionAnswer(string QuestionId, string? OptionId, string? Text);

/// <summary>The durable lifecycle of a clarification request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UserQuestionStatus>))]
public enum UserQuestionStatus
{
    /// <summary>No answer has been accepted.</summary>
    Pending,

    /// <summary>An explicit answer was accepted.</summary>
    Answered,

    /// <summary>The user cancelled the request.</summary>
    Cancelled,

    /// <summary>The live continuation was lost.</summary>
    Interrupted,
}

/// <summary>A host-bound request retained independently of the provider transport.</summary>
public sealed record UserQuestionRequest(
    Guid RequestId,
    SessionId SessionId,
    RunId RunId,
    ToolInvocationId ToolInvocationId,
    string InvocationKey,
    string RepositoryIdentity,
    long PolicyGeneration,
    IReadOnlyList<UserQuestion> Questions,
    DateTimeOffset CreatedAt);

/// <summary>A terminal clarification outcome, carrying no authorization.</summary>
public sealed record UserQuestionResult(UserQuestionStatus Status, IReadOnlyList<UserQuestionAnswer> Answers)
{
    /// <summary>Gets the host-owned explanation of an explicit refusal, distinct from run interruption.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message => Status == UserQuestionStatus.Cancelled ? "The user declined to answer" : null;
}

/// <summary>Bounded immutable waiting/history metadata for ordinary session projections.</summary>
public sealed record UserQuestionProjection(Guid RequestId, RunId RunId, UserQuestionStatus Status, bool Consumed);

/// <summary>Submits explicit local answers to the exact request revision.</summary>
public sealed record SubmitUserQuestionAnswerCommand(SessionId SessionId, Guid RequestId, int ExpectedRevision, IReadOnlyList<UserQuestionAnswer> Answers) : ICommand<bool>;

/// <summary>Cancels the exact clarification request without supplying an answer.</summary>
public sealed record CancelUserQuestionCommand(SessionId SessionId, Guid RequestId, int ExpectedRevision) : ICommand<bool>;

/// <summary>Durable question state; consumed marks delivery to the live tool continuation.</summary>
public sealed record UserQuestionStateChanged(
    SessionId SessionId,
    DateTimeOffset OccurredAt,
    UserQuestionRequest Request,
    UserQuestionResult Result,
    bool Consumed = false) : DomainEvent(SessionId, OccurredAt);

/// <summary>Metadata-only notice published after invocation resources have been released.</summary>
public sealed record UserQuestionReady(SessionId SessionId, DateTimeOffset OccurredAt, RunId RunId, Guid RequestId)
    : DomainEvent(SessionId, OccurredAt);

/// <summary>Host-owned admission boundary; no terminal or provider types cross it.</summary>
public interface IUserQuestionAdmission
{
    /// <summary>Whether a local interactive question adapter is attached.</summary>
    bool IsAvailable { get; }

    /// <summary>Persists a bounded request without waiting for a person.</summary>
    Task<UserQuestionRequest> AdmitAsync(UserQuestionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Code-owned limits shared by admission, answer commands, and restoration.</summary>
public static class UserQuestionValidation
{
    /// <summary>Rejects malformed or oversized requests before storage or presentation.</summary>
    public static void ValidateQuestions(IReadOnlyList<UserQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);
        if (questions.Count is < 1 or > 3 || questions.Any(question => question is null)
            || questions.Select(question => question.Id).Distinct(StringComparer.Ordinal).Count() != questions.Count)
        {
            throw new ArgumentException("A request requires one to three questions with unique ids.", nameof(questions));
        }

        foreach (var question in questions)
        {
            ValidateText(question.Id, 64);
            ValidateText(question.Prompt, 2000);
            ArgumentNullException.ThrowIfNull(question.Options);
            if (question.Options.Count is 1 or > 8 || question.Options.Any(option => option is null)
                || question.Options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != question.Options.Count)
            {
                throw new ArgumentException("Options require zero or two to eight unique alternatives.", nameof(questions));
            }

            foreach (var option in question.Options)
            {
                ValidateText(option.Id, 64);
                ValidateText(option.Label, 300);
            }
        }

        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(questions)) > 16 * 1024)
        {
            throw new ArgumentException("The question payload exceeds 16 KiB.", nameof(questions));
        }
    }

    /// <summary>Requires one explicit choice or text answer for every known question.</summary>
    public static void ValidateAnswers(IReadOnlyList<UserQuestion> questions, IReadOnlyList<UserQuestionAnswer> answers)
    {
        ValidateQuestions(questions);
        ArgumentNullException.ThrowIfNull(answers);
        if (answers.Count != questions.Count || answers.Any(answer => answer is null)
            || answers.Select(answer => answer.QuestionId).Distinct(StringComparer.Ordinal).Count() != answers.Count)
        {
            throw new ArgumentException("Every question requires exactly one answer.", nameof(answers));
        }

        foreach (var answer in answers)
        {
            var question = questions.SingleOrDefault(item => item.Id == answer.QuestionId)
                ?? throw new ArgumentException("Unknown question id.", nameof(answers));
            if ((answer.OptionId is null) == (answer.Text is null))
            {
                throw new ArgumentException("Choose one option or supply free text.", nameof(answers));
            }

            if (answer.OptionId is { } option && !question.Options.Any(item => item.Id == option))
            {
                throw new ArgumentException("Unknown option id.", nameof(answers));
            }

            if (answer.Text is { } text)
            {
                ValidateText(text, 8000);
            }
        }

        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(answers)) > 24 * 1024)
        {
            throw new ArgumentException("The answer payload exceeds 24 KiB.", nameof(answers));
        }
    }

    private static void ValidateText(string text, int maximum)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (text.Length > maximum || text.Any(character => char.IsControl(character) && character is not '\n' and not '\r' and not '\t'))
        {
            throw new ArgumentException("Text exceeds its limit or contains terminal controls.", nameof(text));
        }
    }
}
