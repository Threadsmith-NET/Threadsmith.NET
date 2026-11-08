namespace Threadsmith.Execution;

using System.Net;
using System.Security;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Correlates initial instructions within the existing asynchronous delegation lifetime.</summary>
internal sealed class ChildAgentInstructionObservation : IDisposable
{
    private static readonly AsyncLocal<ChildAgentInstructionObservation?> Ambient = new();
    private readonly ChildAgentInstructionObservation? _previous;
    private readonly DelegationPlan _plan;
    private readonly IDomainEventStream _events;
    private readonly IOutputSanitizer _sanitizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<AgentAssignmentId> _observed = [];
    private readonly Dictionary<AgentAssignmentId, AgentRunInstructionsObserved> _pending = [];

    /// <summary>Initializes a new instance of the <see cref="ChildAgentInstructionObservation"/> class.</summary>
    internal ChildAgentInstructionObservation(DelegationPlan plan, IDomainEventStream events, IOutputSanitizer sanitizer)
    {
        _plan = plan;
        _events = events;
        _sanitizer = sanitizer;
        _previous = Ambient.Value;
        Ambient.Value = this;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Ambient.Value = _previous;
    }

    /// <summary>Publishes only the first effective prepared request, before provider output is possible.</summary>
    internal static Task ObservePreparedAsync(DelegationPlan plan, AgentAssignment assignment, ModelStreamRequest request, CancellationToken cancellationToken)
    {
        var observer = Ambient.Value;
        if (observer is null || observer._plan.DelegationId != plan.DelegationId
            || observer._plan.Provenance != plan.Provenance)
        {
            return Task.CompletedTask;
        }

        return observer.PublishAsync(assignment, AgentInstructionSource.PreparedRequest, () => observer.PreparedSections(request), cancellationToken);
    }

    /// <summary>Ensures even children that never enter their runner have instructions before terminal details.</summary>
    internal Task ObserveAssignmentAsync(AgentAssignmentId assignmentId, CancellationToken cancellationToken)
    {
        var assignment = _plan.Assignments.Single(item => item.AssignmentId == assignmentId);
        return PublishAsync(assignment, AgentInstructionSource.AssignmentOnly, () => RenderAssignment(assignment), cancellationToken);
    }

    private IEnumerable<AgentInstructionSection> PreparedSections(ModelStreamRequest request)
    {
        foreach (var message in request.Messages)
        {
            var label = message.SectionId switch
            {
                "child-host-policy" => "Host instructions",
                "child-role-amendment" => "Role instructions",
                "child-repository-instructions" => "Repository instructions",
                "child-assignment" => "Assignment",
                _ => null,
            };
            if (label is not null)
            {
                yield return new(label, SanitizePreparedText(message.GetModelVisibleContent()));
            }
        }

        if (request.ProviderInstructions is { } provider)
        {
            yield return new("Provider instructions", provider.Content);
        }
    }

    private static IEnumerable<AgentInstructionSection> RenderAssignment(AgentAssignment assignment)
    {
        var text = new StringBuilder()
            .AppendLine(assignment.Objective)
            .AppendLine()
            .AppendLine("Tasks:");
        foreach (var task in assignment.Tasks)
        {
            text.AppendLine(task);
        }

        text.AppendLine().AppendLine("Supplied context:").AppendLine(assignment.InitialContext);
        var scope = assignment.Scope;
        text.AppendLine().AppendLine("Scope:")
            .AppendJoin(Environment.NewLine, scope.Files.Concat(scope.Directories)
                .Concat(scope.Projects).Concat(scope.Symbols).Concat(scope.SharedSurfaces)).AppendLine()
            .AppendLine().AppendLine("Output requirements:").AppendLine(assignment.OutputSchema)
            .AppendLine().AppendLine("Stopping condition:").AppendLine(assignment.StoppingCondition);
        yield return new("Assignment", text.ToString());
    }

    // Prepared prompts XML-escape task and repository content. Redact decoded text nodes,
    // then re-escape only changed nodes so ordinary instruction text remains byte-for-byte exact.
    private string SanitizePreparedText(string text)
    {
        if (!text.Contains('&'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        var position = 0;
        while (position < text.Length)
        {
            var tagStart = text.IndexOf('<', position);
            var textEnd = tagStart < 0 ? text.Length : tagStart;
            var encoded = text[position..textEnd];
            var decoded = WebUtility.HtmlDecode(encoded);
            var sanitized = _sanitizer.Sanitize(decoded);
            result.Append(sanitized == decoded ? encoded : SecurityElement.Escape(sanitized));
            if (tagStart < 0)
            {
                break;
            }

            var tagEnd = text.IndexOf('>', tagStart);
            if (tagEnd < 0)
            {
                result.Append(text.AsSpan(tagStart));
                break;
            }

            result.Append(text.AsSpan(tagStart, tagEnd - tagStart + 1));
            position = tagEnd + 1;
        }

        return result.ToString();
    }

    private (IReadOnlyList<AgentInstructionSection> Sections, bool IsTruncated) RetainSections(IEnumerable<AgentInstructionSection> source)
    {
        var sections = new List<AgentInstructionSection>();
        var remaining = AgentRunInstructionsObserved.MaximumRetainedCharacters;
        foreach (var section in source)
        {
            if (remaining == 0 || sections.Count == AgentRunInstructionsObserved.MaximumRetainedSections)
            {
                return (sections, true);
            }

            // Sanitize before cutting so a credential crossing the boundary cannot leave a partial secret.
            var label = _sanitizer.Sanitize(section.Label);
            var text = _sanitizer.Sanitize(section.Text);
            var retainedLabel = RetainPrefix(label, Math.Min(128, remaining));
            remaining -= retainedLabel.Length;
            var retainedText = RetainPrefix(text, remaining);
            remaining -= retainedText.Length;
            sections.Add(new(retainedLabel, retainedText));
            if (retainedLabel.Length != label.Length || retainedText.Length != text.Length)
            {
                return (sections, true);
            }
        }

        return (sections, false);
    }

    private static string RetainPrefix(string text, int maximumCharacters)
    {
        if (text.Length <= maximumCharacters)
        {
            return text;
        }

        var length = maximumCharacters;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }

    private async Task PublishAsync(AgentAssignment assignment, AgentInstructionSource source, Func<IEnumerable<AgentInstructionSection>> createSections, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_observed.Contains(assignment.AssignmentId))
            {
                return;
            }

            if (!_pending.TryGetValue(assignment.AssignmentId, out var observation))
            {
                var retained = RetainSections(createSections());
                observation = new AgentRunInstructionsObserved(
                    _plan.Provenance.SessionId,
                    DateTimeOffset.UtcNow,
                    _plan.DelegationId,
                    assignment.AssignmentId,
                    assignment.ChildRunId,
                    _plan.Provenance.Generation,
                    source,
                    retained.Sections)
                {
                    IsTruncated = retained.IsTruncated,
                };
                _pending.Add(assignment.AssignmentId, observation);
            }

            await _events.PublishAsync(observation, cancellationToken);
            _observed.Add(assignment.AssignmentId);
            _pending.Remove(assignment.AssignmentId);
        }
        finally
        {
            _gate.Release();
        }
    }
}
