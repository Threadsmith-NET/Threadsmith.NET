namespace Threadsmith.Interaction.Presentation;

using Threadsmith.Core;

/// <summary>Projects tool, MCP, and skill events into the existing shared operation activity surface.</summary>
internal sealed class InteractionOperationActivities
{
    private readonly TimeProvider _timeProvider;
    private readonly bool _showDurations;
    private readonly Dictionary<object, Entry> _entries = [];
    private readonly Dictionary<SkillInvocationId, SkillWorkflowCheckpointWritten> _skills = [];

    /// <summary>Initializes a new instance of the <see cref="InteractionOperationActivities"/> class.</summary>
    internal InteractionOperationActivities(TimeProvider timeProvider, bool showDurations)
    {
        _timeProvider = timeProvider;
        _showDurations = showDurations;
    }

    /// <summary>Gets all active operations for the existing retained surface.</summary>
    internal IReadOnlyList<InteractionActivity> Activities => _entries.Values.Select(item => item.Activity).ToArray();

    /// <summary>Gets tool identities with ordinary agent progress.</summary>
    internal IEnumerable<ToolInvocationId> ToolIds => _entries.Keys.OfType<ToolInvocationId>();

    /// <summary>Gets the live activity affected by an event for sequential frontends.</summary>
    internal InteractionActivity? ActivityFor(IDomainEvent domainEvent)
    {
        object? key = domainEvent switch
        {
            ToolInvocationStarted started => started.ToolInvocationId,
            SkillWorkflowCheckpointWritten skill => Owner(skill),
            SkillInvocationProgressObserved progress when _skills.TryGetValue(progress.InvocationId, out var skill) => Owner(skill),
            _ => null,
        };
        return key is not null && _entries.TryGetValue(key, out var entry) ? entry.Activity : null;
    }

    /// <summary>Applies one lifecycle or progress event without introducing another display owner.</summary>
    internal bool Observe(IDomainEvent domainEvent)
    {
        switch (domainEvent)
        {
            case ToolInvocationStarted started:
                _entries[started.ToolInvocationId] = new(
                    started.RunId,
                    InteractionPresentationFormatter.CreateToolActivity(started, _timeProvider, _showDurations));
                return true;
            case ToolInvocationCompleted completed:
                return _entries.Remove(completed.ToolInvocationId);
            case RunCompleted completed:
                var keys = _entries.Where(pair => pair.Value.RunId == completed.RunId).Select(pair => pair.Key).ToArray();
                foreach (var key in keys)
                {
                    _entries.Remove(key);
                }

                foreach (var id in _skills.Where(pair => pair.Value.RunId == completed.RunId).Select(pair => pair.Key).ToArray())
                {
                    _skills.Remove(id);
                }

                return keys.Length > 0;
            case SkillWorkflowCheckpointWritten skill:
                if (_skills.TryGetValue(skill.InvocationId, out var previous)
                    && (skill.Generation < previous.Generation
                        || (skill.Generation == previous.Generation && EndsSkillActivity(previous.Status))))
                {
                    return false;
                }

                _skills[skill.InvocationId] = skill;
                if (_skills.Count > 1024)
                {
                    foreach (var id in _skills.Where(pair => EndsSkillActivity(pair.Value.Status))
                        .OrderBy(pair => pair.Value.OccurredAt).Take(_skills.Count - 1024).Select(pair => pair.Key).ToArray())
                    {
                        _skills.Remove(id);
                    }
                }

                if (EndsSkillActivity(skill.Status))
                {
                    return skill.InvokingToolInvocationId is null && _entries.Remove(skill.InvocationId);
                }

                var owner = Owner(skill);
                if (!_entries.ContainsKey(owner) && skill.InvokingToolInvocationId is null)
                {
                    _entries[owner] = new(skill.RunId, InteractionPresentationFormatter.CreateOperationActivity(
                        "SKILLS", $"{skill.SkillId.Value}@{skill.Version}", $"Invocation: {skill.InvocationId.Value:D}", _timeProvider, _showDurations));
                }

                return _entries.ContainsKey(owner);
            case SkillInvocationProgressObserved progress when _skills.TryGetValue(progress.InvocationId, out var skill)
                && skill.Generation == progress.Generation && !EndsSkillActivity(skill.Status)
                && _entries.TryGetValue(Owner(skill), out var current):
                current.HostProgress = [new PresentationTextSegment(progress.Message, PresentationTextRole.Muted)];
                current.Refresh();
                return true;
            default:
                return false;
        }
    }

    /// <summary>Combines existing delegation progress with host preparation progress.</summary>
    internal void SetToolProgress(ToolInvocationId id, IReadOnlyList<PresentationTextSegment> progress)
    {
        if (_entries.TryGetValue(id, out var entry))
        {
            entry.AgentProgress = progress;
            entry.Refresh();
        }
    }

    /// <summary>Recognizes an invocation's terminal or suspended boundary.</summary>
    internal static bool EndsSkillActivity(SkillInvocationStatus status) => status is
        SkillInvocationStatus.Completed or SkillInvocationStatus.Failed or SkillInvocationStatus.Cancelled or SkillInvocationStatus.AwaitingHost;

    private static object Owner(SkillWorkflowCheckpointWritten skill) =>
        skill.InvokingToolInvocationId is { } tool ? tool : skill.InvocationId;

    private sealed class Entry
    {
        internal Entry(RunId? runId, InteractionActivity activity)
        {
            RunId = runId;
            Activity = activity;
        }

        internal RunId? RunId { get; }

        internal InteractionActivity Activity { get; private set; }

        internal IReadOnlyList<PresentationTextSegment> HostProgress { get; set; } = [];

        internal IReadOnlyList<PresentationTextSegment> AgentProgress { get; set; } = [];

        internal void Refresh() => Activity = Activity with { ToolProgress = [.. HostProgress, .. AgentProgress] };
    }
}
