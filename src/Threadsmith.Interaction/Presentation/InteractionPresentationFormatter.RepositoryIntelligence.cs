namespace Threadsmith.Interaction.Presentation;

using System.Text;
using System.Text.Json;

/// <summary>Projects repository results through the existing bounded tool completion renderer.</summary>
internal static partial class InteractionPresentationFormatter
{
    private const int MaximumRepositoryResultCharacters = 1024 * 1024;
    private const int MaximumRepositorySummaryEntries = 12;

    private static string GetRepositoryIntelligenceOutput(string? json, int maximumCharacters)
    {
        var output = new StringBuilder();
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumRepositoryResultCharacters)
        {
            output.Append("Repository intelligence result exceeds the interactive summary limit or is unavailable.");
        }
        else
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                var investigation = RepositoryObject(root, "Investigation");
                if (investigation.ValueKind == JsonValueKind.Object)
                {
                    AppendRepositoryInvestigation(output, investigation);
                }
                else if (RepositoryObject(root, "Profile") is { ValueKind: JsonValueKind.Object } profile)
                {
                    AppendRepositoryProfile(output, profile);
                }
                else
                {
                    var controls = RepositoryObject(root, "Controls");
                    output.Append("Controls: persistence=").Append(RepositoryControl(controls, "Persistence"))
                        .Append(", archeology=").Append(RepositoryControl(controls, "Archeology"))
                        .Append(", recall=").Append(RepositoryControl(controls, "Recall"))
                        .Append(", maintenance=").AppendLine(RepositoryControl(controls, "Maintenance"));
                    output.Append("Investigation inference: ").AppendLine(RepositoryObject(root, "AnalysisAvailable").ValueKind switch
                    {
                        JsonValueKind.True => "available",
                        JsonValueKind.False => "unavailable",
                        _ => "unspecified",
                    });
                    if (RepositoryString(controls, "DisabledReason") is { Length: > 0 } warning)
                    {
                        output.Append("Control settings warning: ").AppendLine(warning);
                    }

                    output.Append(RepositoryString(root, "Reason") ?? "Repository intelligence status is unavailable.");
                }
            }
            catch (JsonException)
            {
                output.Append("Repository intelligence result could not be displayed.");
            }
        }

        return PrepareBoundedOutput(output.ToString().TrimEnd(), maximumCharacters, "\n[Repository intelligence summary truncated by display limit]");
    }

    private static void AppendRepositoryInvestigation(StringBuilder output, JsonElement investigation)
    {
        var interpretation = RepositoryObject(investigation, "Interpretation");
        var outcome = RepositoryString(interpretation, "Outcome") ?? "Unavailable";
        output.AppendLine(RepositoryString(interpretation, "Answer") ?? (outcome == "Unavailable"
            ? "Interpretation unavailable; collected evidence is listed below."
            : "No evidence-backed answer was returned."));
        output.Append("Outcome: ").AppendLine(outcome);
        var assessment = RepositoryObject(interpretation, "AnswerAssessment");
        if (RepositoryString(assessment, "EvidenceClass") is { } evidenceClass)
        {
            output.Append("Basis: ").Append(evidenceClass).Append("; applicability: ")
                .Append(RepositoryString(assessment, "Applicability") ?? "unspecified").Append("; confidence: ")
                .AppendLine(RepositoryString(assessment, "Confidence") ?? "unspecified");
        }

        if (RepositoryString(assessment, "Uncertainty") is { Length: > 0 } uncertainty)
        {
            output.Append("Uncertainty: ").AppendLine(uncertainty);
        }

        AppendRepositoryAssessment(output, assessment);

        AppendRepositoryList(output, interpretation, "Limitations", "Limitations");
        AppendRepositoryOmissions(output, investigation);
        AppendRepositoryCandidates(output, interpretation);
        AppendRepositorySources(output, investigation, "SupportingEvidenceIds", "Sources", outcome == "Unavailable");
        AppendRepositorySources(output, investigation, "ConflictingEvidenceIds", "Conflicting sources", false);
        var target = RepositoryObject(investigation, "Target");
        if (RepositoryString(target, "Commit") is { } commit)
        {
            output.Append("Snapshot: ").AppendLine(RepositoryRevision(commit));
        }

        if (RepositoryObject(investigation, "PendingChanges").ValueKind == JsonValueKind.True)
        {
            output.AppendLine("Snapshot warning: changes or mutable observations were reported; results are not guaranteed to describe the current checkout.");
            var after = RepositoryObject(investigation, "AfterInvestigation");
            output.Append("Final checkout snapshot: ")
                .AppendLine(RepositoryRevision(RepositoryString(after, "Head") ?? "unavailable"));
        }

        var selection = RepositoryObject(investigation, "Selection");
        var consumption = RepositoryObject(investigation, "Consumption");
        if (RepositoryObject(selection, "IncludeHistory").ValueKind == JsonValueKind.True)
        {
            output.Append("History: examined ").Append(RepositoryNumber(consumption, "Commits"))
                .Append(" of at most ").Append(RepositoryNumber(selection, "MaximumCommits"))
                .AppendLine(" recent repository commits; coverage is limited to the selected scope and frontier.");
            output.Append("History coverage: ").AppendLine(RepositoryString(investigation, "HistoryCoverage") switch
            {
                "RecentRepositoryCommitFrontier;SemanticCoverageUnassessed" => "Recent repository commits only; completeness of the historical explanation is not established.",
                "NotRequested" => "Not requested.",
                "Unavailable" or null => "Unavailable.",
                var coverage => coverage,
            });
        }

        var model = RepositoryObject(investigation, "Model");
        if (RepositoryString(model, "Model") is { } modelName)
        {
            output.Append("Model: ").Append(modelName).Append("; reasoning: ")
                .AppendLine(RepositoryString(model, "Reasoning") ?? "default");
        }

        output.Append("Usage: ").Append(RepositoryNumber(investigation, "ModelCalls"))
            .Append('/').Append(RepositoryNumber(selection, "MaximumModelCalls"))
            .Append(" model calls; files: ").Append(RepositoryNumber(consumption, "Files"))
            .Append("; commits: ").Append(RepositoryNumber(consumption, "Commits")).AppendLine(".");
        output.Append("No persistent intelligence saved; further evidence reads require a new investigation.");
    }

    private static void AppendRepositoryProfile(StringBuilder output, JsonElement profile)
    {
        output.Append("Deterministic profile: ").Append(RepositoryNumber(profile, "InspectedFiles"))
            .Append(" files inspected; ").Append(RepositoryNumber(profile, "AdmittedBytes")).AppendLine(" content bytes admitted.");
        var snapshot = RepositoryObject(profile, "Snapshot");
        output.Append("Snapshot: ").AppendLine(RepositoryRevision(RepositoryString(snapshot, "Commit") ?? "unavailable"));
        if (RepositoryObject(profile, "PendingChanges").ValueKind == JsonValueKind.True)
        {
            output.AppendLine("Snapshot warning: changes or mutable observations were reported.");
        }

        var facts = RepositoryObject(profile, "Facts");
        if (facts.ValueKind == JsonValueKind.Array)
        {
            output.Append("Static facts: ").Append(facts.GetArrayLength()).AppendLine();
            foreach (var fact in facts.EnumerateArray().Take(MaximumRepositorySummaryEntries))
            {
                output.Append("- ").Append(RepositoryString(fact, "Path")).Append(": ")
                    .Append(RepositoryString(fact, "Name")).Append(" = ").AppendLine(RepositoryString(fact, "Value") ?? "declared");
            }

            if (facts.GetArrayLength() > MaximumRepositorySummaryEntries)
            {
                output.Append("- ").Append(facts.GetArrayLength() - MaximumRepositorySummaryEntries).AppendLine(" additional facts in the structured result.");
            }
        }

        AppendRepositoryOmissions(output, profile);
    }

    private static void AppendRepositoryOmissions(StringBuilder output, JsonElement result)
    {
        var omissions = RepositoryObject(result, "Omissions");
        if (omissions.ValueKind != JsonValueKind.Array || omissions.GetArrayLength() == 0)
        {
            return;
        }

        output.AppendLine("Collection limits:");
        foreach (var omission in omissions.EnumerateArray().Take(MaximumRepositorySummaryEntries))
        {
            var reason = RepositoryString(omission, "Reason") ?? "Unspecified coverage limitation";
            reason = reason.StartsWith("Profile:", StringComparison.Ordinal) ? reason[8..] : reason;
            output.Append("- ").Append(reason switch
            {
                "StaticDeclarationsOnly" => "Metadata contains static declarations; builds were not evaluated.",
                "ImmutableSemanticsUnavailable" => "Immutable semantic analysis was unavailable.",
                "SemanticInterpretationNotPerformed" => "The collection stage does not infer intent.",
                "TestSourcesAreNotExecutionEvidence" => "Test source does not establish that tests passed.",
                "CumulativeCommitLimit" => "The history commit limit was reached.",
                "CommitOutsideScopeOrNoVisibleChanges" => "Examined commits had no visible changes in the selected scope.",
                "DiffsAreChangedPathMetadata;HistoricalBodiesUseBoundedExpansion" => "History diffs contain changed-path metadata; historical content requires evidence expansion.",
                _ => reason,
            });
            var locator = RepositoryString(omission, "Locator") ?? RepositoryString(omission, "Path");
            if (!string.IsNullOrWhiteSpace(locator))
            {
                output.Append(" (").Append(locator).Append(')');
            }

            output.AppendLine();
        }

        if (omissions.GetArrayLength() > MaximumRepositorySummaryEntries)
        {
            output.Append("- ").Append(omissions.GetArrayLength() - MaximumRepositorySummaryEntries).AppendLine(" additional collection limits in the structured result.");
        }
    }

    private static void AppendRepositoryCandidates(StringBuilder output, JsonElement interpretation)
    {
        var candidates = RepositoryObject(interpretation, "Candidates");
        if (candidates.ValueKind == JsonValueKind.Array && candidates.GetArrayLength() > 0)
        {
            output.AppendLine("Assessed findings:");
            foreach (var candidate in candidates.EnumerateArray().Take(MaximumRepositorySummaryEntries))
            {
                output.Append("- ").Append(RepositoryString(candidate, "Title")).Append(" [")
                    .Append(RepositoryString(candidate, "Kind")).Append("; ")
                    .Append(RepositoryString(candidate, "Applicability")).Append("; ")
                    .Append(RepositoryString(candidate, "EvidenceClass")).Append("; confidence: ")
                    .Append(RepositoryString(candidate, "Confidence")).AppendLine("]");
                output.AppendLine(RepositoryString(candidate, "Statement"));
                AppendRepositoryAssessment(output, candidate);
                if (RepositoryString(candidate, "Uncertainty") is { Length: > 0 } uncertainty)
                {
                    output.Append("Uncertainty: ").AppendLine(uncertainty);
                }
            }

            if (candidates.GetArrayLength() > MaximumRepositorySummaryEntries)
            {
                output.Append("- ").Append(candidates.GetArrayLength() - MaximumRepositorySummaryEntries).AppendLine(" additional findings in the structured result.");
            }
        }

        var relationships = RepositoryObject(interpretation, "Relationships");
        if (relationships.ValueKind == JsonValueKind.Array && relationships.GetArrayLength() > 0)
        {
            output.Append("Assessed relationships: ").Append(relationships.GetArrayLength()).AppendLine(" (details in the structured result).");
        }
    }

    private static void AppendRepositoryAssessment(StringBuilder output, JsonElement assessment)
    {
        foreach (var (property, label) in new[] { ("Then", "Earlier"), ("Replacement", "Replacement"), ("AtTarget", "At pinned target") })
        {
            if (RepositoryString(assessment, property) is { Length: > 0 } text)
            {
                output.Append(label).Append(": ").AppendLine(text);
            }
        }
    }

    private static void AppendRepositorySources(StringBuilder output, JsonElement investigation, string identifiersProperty, string label, bool collected)
    {
        var interpretation = RepositoryObject(investigation, "Interpretation");
        var identifiers = RepositoryObject(interpretation, identifiersProperty);
        var selected = identifiers.ValueKind == JsonValueKind.Array
            ? identifiers.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? string.Empty).ToHashSet(StringComparer.Ordinal)
            : [];
        var candidates = RepositoryObject(interpretation, "Candidates");
        if (candidates.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in candidates.EnumerateArray())
            {
                var candidateIds = RepositoryObject(candidate, identifiersProperty == "SupportingEvidenceIds" ? "EvidenceIds" : "ConflictingEvidenceIds");
                if (candidateIds.ValueKind == JsonValueKind.Array)
                {
                    foreach (var id in candidateIds.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String))
                    {
                        selected.Add(id.GetString() ?? string.Empty);
                    }
                }
            }
        }

        var evidence = RepositoryObject(investigation, "Evidence");
        if (evidence.ValueKind != JsonValueKind.Array || (!collected && selected.Count == 0))
        {
            return;
        }

        var count = 0;
        foreach (var item in evidence.EnumerateArray())
        {
            if (!collected && !selected.Contains(RepositoryString(item, "Id") ?? string.Empty))
            {
                continue;
            }

            var source = RepositoryObject(item, "Source");
            if (RepositoryString(source, "Path") is not { } path)
            {
                continue;
            }

            if (count++ == 0)
            {
                output.AppendLine(collected ? "Collected evidence (not interpreted):" : label + ":");
            }

            if (count <= MaximumRepositorySummaryEntries)
            {
                output.Append("- ").Append(path).Append(" @ ")
                    .Append(RepositoryRevision(RepositoryString(source, "Revision") ?? "working tree"));
                var start = RepositoryNumber(item, "StartLine");
                var end = RepositoryNumber(item, "EndLine");
                if (start > 0 && end >= start)
                {
                    output.Append(':').Append(start).Append('-').Append(end);
                }

                if (RepositoryString(item, "State") is { } state && state != "Complete")
                {
                    output.Append(" [").Append(state).Append(']');
                }

                output.AppendLine();
            }
        }

        if (count > MaximumRepositorySummaryEntries)
        {
            output.Append("- ").Append(count - MaximumRepositorySummaryEntries).AppendLine(" additional sources in the structured result.");
        }
    }

    private static void AppendRepositoryList(StringBuilder output, JsonElement element, string property, string label)
    {
        var array = RepositoryObject(element, property);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
        {
            return;
        }

        output.Append(label).AppendLine(":");
        foreach (var item in array.EnumerateArray().Take(MaximumRepositorySummaryEntries))
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                output.Append("- ").AppendLine(item.GetString());
            }
        }

        if (array.GetArrayLength() > MaximumRepositorySummaryEntries)
        {
            output.Append("- ").Append(array.GetArrayLength() - MaximumRepositorySummaryEntries).AppendLine(" additional limitations in the structured result.");
        }
    }

    private static JsonElement RepositoryObject(JsonElement element, string property)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) ? value : default;
    }

    private static string? RepositoryString(JsonElement element, string property)
    {
        var value = RepositoryObject(element, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static int RepositoryNumber(JsonElement element, string property)
    {
        var value = RepositoryObject(element, property);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;
    }

    private static string RepositoryControl(JsonElement controls, string property)
    {
        return RepositoryObject(controls, property).ValueKind switch
        {
            JsonValueKind.True => "on",
            JsonValueKind.False => "off",
            _ => "unavailable",
        };
    }

    private static string RepositoryRevision(string revision)
    {
        return revision.Length == 40 && revision.All(Uri.IsHexDigit) ? revision[..12] : revision;
    }
}
