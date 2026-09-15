namespace Threadsmith.Models;

using Threadsmith.Core;

/// <summary>Source range in already-rendered message text; contains no duplicate content.</summary>
public sealed record ModelContextSource(string Category, string Label, int Start, int Length);

/// <summary>Size-only attribution helpers shared by existing estimators and adapters.</summary>
public static class ContextUsageAttribution
{
    /// <summary>Maps host section identities, without inspecting or guessing from message prose.</summary>
    public static string Category(ModelMessage message) => message.SectionId switch
    {
        "host-policy" => "System prompt",
        "phase-policy" => "Phase instructions",
        "repository-instructions" => "Repository instructions",
        "repository-memory" => "Memories",
        "governed-request-state" => "Task and governed state",
        "provider-replay" => "Provider overhead/replay",
        _ when message.SectionId.Contains("summary", StringComparison.Ordinal)
            || message.SectionId.Contains("compaction", StringComparison.Ordinal) => "Compaction summary",
        _ when message.Role is ModelMessageRole.User or ModelMessageRole.Assistant or ModelMessageRole.Tool => "Conversation",
        _ => "Additional instructions",
    };

    /// <summary>Subdivides a message using ordered source ranges and one cumulative measurement basis.</summary>
    public static ContextUsageComponent Message(ModelMessage message, int ordinal, string container, long tokens, Func<int, long> measurePrefix)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(measurePrefix);
        var category = Category(message);
        var label = message.ToolName is { } tool ? $"{message.Role}: {tool}" : $"{message.Role}: {message.SectionId}";
        var parent = new ContextUsageComponent($"{container}:{ordinal}", category, label, container, tokens);
        if (message.Sources.Count == 0)
        {
            return parent;
        }

        var children = new List<ContextUsageComponent>();
        var position = 0;
        long allocated = 0;
        foreach (var source in message.Sources)
        {
            if (source.Start < position || source.Length < 0 || source.Start > message.GetModelVisibleContentLength() - source.Length)
            {
                throw new InvalidOperationException("Context source ranges must be ordered and inside their message.");
            }

            Add(category, "Section framing", measurePrefix(source.Start) - measurePrefix(position));
            Add(source.Category, source.Label, measurePrefix(source.Start + source.Length) - measurePrefix(source.Start));
            position = source.Start + source.Length;
        }

        Add(category, "Section framing / remaining content", tokens - allocated);
        return parent with { Children = children.AsReadOnly() };

        void Add(string childCategory, string childLabel, long count)
        {
            if (count < 0)
            {
                throw new InvalidOperationException("Context attribution exceeds its measured parent.");
            }

            if (count > 0)
            {
                children.Add(new ContextUsageComponent($"{parent.Id}:{children.Count}", childCategory, childLabel, container, count));
                allocated += count;
            }
        }
    }
}
