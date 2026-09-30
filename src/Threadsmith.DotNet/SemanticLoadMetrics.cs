namespace Threadsmith.DotNet;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using Threadsmith.Core;

/// <summary>Low-cardinality host telemetry for semantic workspace loading.</summary>
internal static class SemanticLoadMetrics
{
    private static readonly Meter _meter = new("Threadsmith.SemanticLoad", "1.0");

    /// <summary>Gets terminal load counts by mode, temperature, confidence, and outcome.</summary>
    public static Counter<long> Loads { get; } = _meter.CreateCounter<long>(
        "threadsmith.semantic.load.count");

    /// <summary>Gets end-to-end semantic load duration.</summary>
    public static Histogram<double> TotalDuration { get; } = _meter.CreateHistogram<double>(
        "threadsmith.semantic.load.duration",
        "ms");

    /// <summary>Gets MSBuild workspace evaluation duration.</summary>
    public static Histogram<double> EvaluationDuration { get; } = _meter.CreateHistogram<double>(
        "threadsmith.semantic.load.evaluation.duration",
        "ms");

    /// <summary>Gets repository confinement and inventory duration.</summary>
    public static Histogram<double> ConfinementDuration { get; } = _meter.CreateHistogram<double>(
        "threadsmith.semantic.load.confinement.duration",
        "ms");

    /// <summary>Gets aggregate initial compilation-preparation duration.</summary>
    public static Histogram<double> CompilationDuration { get; } = _meter.CreateHistogram<double>(
        "threadsmith.semantic.load.compilation.duration",
        "ms");

    /// <summary>Gets bounded project-count observations for terminal loads.</summary>
    public static Histogram<long> ProjectCount { get; } = _meter.CreateHistogram<long>(
        "threadsmith.semantic.load.projects");

    /// <summary>Gets workspace-failure observations for terminal loads.</summary>
    public static Histogram<long> WorkspaceFailures { get; } = _meter.CreateHistogram<long>(
        "threadsmith.semantic.load.workspace_failures");

    /// <summary>Gets process working-set observations at terminal load.</summary>
    public static Histogram<long> WorkingSet { get; } = _meter.CreateHistogram<long>(
        "threadsmith.semantic.load.working_set",
        "By");

    /// <summary>Gets generation warming and demand wait durations through the existing load meter.</summary>
    internal static Histogram<double> PreparationDuration { get; } = _meter.CreateHistogram<double>("threadsmith.semantic.preparation.duration", "ms");

    /// <summary>Gets bounded preparation count observations.</summary>
    internal static Histogram<long> PreparationCount { get; } = _meter.CreateHistogram<long>("threadsmith.semantic.preparation.count");

    /// <summary>Gets cancellation counts by lifetime owner.</summary>
    internal static Counter<long> PreparationCancellations { get; } = _meter.CreateCounter<long>("threadsmith.semantic.preparation.cancellations");

    /// <summary>Gets actual late compiler results discarded after cancellation.</summary>
    internal static Counter<long> PreparationDiscarded { get; } = _meter.CreateCounter<long>("threadsmith.semantic.preparation.discarded");

    /// <summary>Records one terminal semantic load without repository-specific tags.</summary>
    public static void Record(
        string mode,
        string temperature,
        string outcome,
        SemanticConfidenceLevel confidence,
        TimeSpan totalDuration,
        TimeSpan evaluationDuration,
        TimeSpan confinementDuration,
        TimeSpan compilationDuration,
        int expectedProjects,
        int loadedProjects,
        int excludedProjects,
        int failedProjects,
        int compiledProjects,
        int workspaceFailures,
        long workingSetBytes)
    {
        var tags = new TagList
        {
            { "mode", mode },
            { "temperature", temperature },
            { "outcome", outcome },
            { "confidence", confidence.ToString() },
        };
        Loads.Add(1, tags);
        TotalDuration.Record(totalDuration.TotalMilliseconds, tags);
        EvaluationDuration.Record(evaluationDuration.TotalMilliseconds, tags);
        ConfinementDuration.Record(confinementDuration.TotalMilliseconds, tags);
        CompilationDuration.Record(compilationDuration.TotalMilliseconds, tags);
        WorkspaceFailures.Record(workspaceFailures, tags);
        WorkingSet.Record(workingSetBytes, tags);
        ProjectCount.Record(expectedProjects, AddProjectKind(tags, "expected"));
        ProjectCount.Record(loadedProjects, AddProjectKind(tags, "loaded"));
        ProjectCount.Record(excludedProjects, AddProjectKind(tags, "excluded"));
        ProjectCount.Record(failedProjects, AddProjectKind(tags, "failed"));
        ProjectCount.Record(compiledProjects, AddProjectKind(tags, "compiled"));
    }

    private static TagList AddProjectKind(TagList commonTags, string kind)
    {
        TagList projectTags = default;
        foreach (var tag in commonTags)
        {
            projectTags.Add(tag);
        }

        projectTags.Add("kind", kind);
        return projectTags;
    }
}
