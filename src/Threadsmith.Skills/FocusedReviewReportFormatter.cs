namespace Threadsmith.Skills;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Renders the fixed review report from accepted specialist data without another model formatting pass.</summary>
public static class FocusedReviewReportFormatter
{
    /// <summary>Produces canonical Markdown with honest coverage and complete optional criterion accounting.</summary>
    public static string Render(
        FocusedReviewTarget target,
        IReadOnlyList<AgentRunOutcome> outcomes,
        bool inboxLinks)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(outcomes);
        var accepted = outcomes.Where(
            outcome => outcome.Status == AgentRunStatus.Completed && outcome.FocusedReviewValidated && outcome.Response is not null)
            .OrderBy(outcome => outcome.Role).Select(
                outcome => new AcceptedResult(
                outcome.Role,
                JsonSerializer.Deserialize<JsonElement>(outcome.Response ?? "{}"))).ToArray();
        var complete = accepted.Length == 4 && target.Exclusions.Count == 0;
        var builder = new StringBuilder();
        builder.Append("# Code review\n\nScope: ").Append(Escape(target.Mode)).Append("; ").Append(
            Escape(target.Repository))
            .Append("; ").Append(Escape(target.Branch ?? "explicit scope")).Append(
                "; ")
            .Append(target.MergeBase is null ? "snapshot audit" : "change review").Append("; revision ").Append(
                Escape(target.Revision))
            .Append("; comparison ").Append(Escape(target.MergeBase ?? "none")).Append("; snapshot ").Append(Escape(target.Identity)).Append('\n');
        builder.Append("Review status: ").Append(complete ? "complete" : "partial").Append("; static advisory inspection; no tests or benchmarks executed.\n");
        if (target.Instructions.Length > 0)
        {
            builder.Append("Review objective: ").Append(Escape(target.Instructions)).Append('\n');
        }

        foreach (var outcome in outcomes.OrderBy(outcome => outcome.Role))
        {
            builder.Append("- ").Append(outcome.Role).Append(": ").Append(outcome.Status);
            if (!outcome.FocusedReviewValidated)
            {
                builder.Append("; ").Append(Escape(outcome.Reason));
            }

            builder.Append('\n');
        }

        foreach (var exclusion in target.Exclusions)
        {
            builder.Append("- Omitted: ").Append(Escape(exclusion)).Append('\n');
        }

        foreach (var result in accepted)
        {
            foreach (var coverage in result.Data.GetProperty("coverage").EnumerateArray())
            {
                builder.Append("- ").Append(result.Role).Append(" coverage: ").Append(Escape(coverage.GetString() ?? string.Empty)).Append('\n');
            }
        }

        builder.Append("\n## What's good\n\n");
        AppendNotes(
            "strengths",
            accepted,
            target,
            builder,
            inboxLinks,
            "Insufficient inspected evidence to identify supported strengths.");
        builder.Append("\n## Overall architectural soundness\n\n");
        var architecture = accepted.Where(item => item.Role == AgentRole.ArchitectureReviewer).ToArray();
        AppendNotes(
            "architecture",
            architecture,
            target,
            builder,
            inboxLinks,
            "Insufficient architecture-review evidence to assess overall soundness. No positive verdict is assumed.");
        builder.Append("\n## Possible issues\n");
        var issues = accepted.SelectMany(
            result => result.Data.GetProperty("issues").EnumerateArray()
                .Select(issue => new Issue(result.Role, issue)))
            .GroupBy(
                issue => IssueKey(issue.Data),
                StringComparer.Ordinal)
            .Select(
                group => (Issue: group.First().Data, Roles: string.Join(", ", group.Select(item => item.Role).Distinct().Order())))
            .OrderBy(
                item => Text(item.Issue.GetProperty("location"), "path"),
                StringComparer.Ordinal)
            .ThenBy(
                item => item.Issue.GetProperty("location").GetProperty("startLine").GetInt32())
            .ThenBy(item => Text(item.Issue, "title"), StringComparer.Ordinal).ToArray();
        var issueNumber = 0;
        foreach (var priority in new[] { "P1", "P2", "P3" })
        {
            builder.Append("\n### ").Append(priority).Append("\n\n");
            var group = issues.Where(item => Text(item.Issue, "priority") == priority).ToArray();
            if (group.Length == 0)
            {
                builder.Append("No supported ").Append(priority).Append(" issues identified.\n");
            }

            foreach (var (issue, roles) in group)
            {
                builder.Append("- **I").Append(++issueNumber).Append(": ").Append(Escape(Text(issue, "title"))).Append(
                    "** — ")
                    .Append(Citation(issue.GetProperty("location"), target, inboxLinks)).Append(" (").Append(roles).Append(
                        ")\n")
                    .Append("  Trigger / evidence: ").Append(Escape(Text(issue, "trigger"))).Append(
                        ' ')
                    .Append(Citations(issue, target, inboxLinks)).Append("\n  Consequence: ").Append(
                        Escape(Text(issue, "consequence")))
                    .Append("\n  Suggested action: ").Append(
                        Escape(Text(issue, "recommendation")))
                    .Append("\n  Uncertainty: ").Append(Escape(Text(issue, "uncertainty"))).Append('\n');
            }
        }

        builder.Append("\n## Observations\n\n");
        var observations = accepted.SelectMany(
            result => result.Data.GetProperty("observations").EnumerateArray()
            .Select(item => (result.Role, Item: item))).ToArray();
        if (observations.Length == 0)
        {
            builder.Append("No additional observations.\n");
        }

        foreach (var (role, item) in observations)
        {
            builder.Append("- ").Append(Escape(Text(item, "text"))).Append(
                " Assumption / uncertainty: ")
                .Append(Escape(Text(item, "assumption"))).Append(" (").Append(role).Append(
                    "). ")
                .Append(Citations(item, target, inboxLinks)).Append('\n');
        }

        if (target.Requirements is { } requirements)
        {
            builder.Append("\n## Acceptance criteria assessment\n\nSource: ").Append(Escape(requirements.Source)).Append(
                "; ")
                .Append(Escape(requirements.Path)).Append("; digest ").Append(Escape(requirements.Digest)).Append(".\n\n");
            if (requirements.Criteria.Count == 0)
            {
                builder.Append("No explicit acceptance criteria were found. No inferred criteria have been substituted.\n");
            }
            else
            {
                builder.Append("| Criterion | Status | Evidence | Assessment / gap |\n| --- | --- | --- | --- |\n");
                foreach (var criterion in requirements.Criteria)
                {
                    var assessments = accepted.SelectMany(
                        result => result.Data.GetProperty("criteria").EnumerateArray()
                        .Where(item => Text(item, "criterionId") == criterion.Id).Select(item => (result.Role, Item: item))).ToArray();
                    var statuses = assessments.Select(item => Text(item.Item, "status")).Distinct(StringComparer.Ordinal).ToArray();
                    var status = statuses.Length > 1 ? "Ambiguous" : statuses.SingleOrDefault() ?? "Not assessed";
                    if (status == "Met" && (criterion.RequiresExecution || assessments.Length != 4 || !complete))
                    {
                        status = "Partially met";
                    }

                    var gap = assessments.Length < 4 || !complete ? "Incomplete reviewer coverage. " : string.Empty;
                    if (criterion.RequiresExecution)
                    {
                        gap += "Runtime/manual evidence is unavailable. ";
                    }

                    if (assessments.Length == 0)
                    {
                        gap += "No accepted reviewer assessed this criterion.";
                    }

                    builder.Append("| ").Append(
                        Escape($"{criterion.Id} ({requirements.Path}:L{criterion.Line}): {criterion.Text}"))
                        .Append(" | ").Append(status).Append(
                            " | ")
                        .AppendJoin(
                            "; ",
                            assessments.Select(item => Citations(item.Item, target, inboxLinks)).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal))
                        .Append(" | ").Append(
                            Escape(gap + string.Join("; ", assessments.Select(item => $"{item.Role}: {Text(item.Item, "assessment")}"))))
                        .Append(" |\n");
                }
            }
        }

        return builder.ToString();
    }

    private static string IssueKey(JsonElement item) => string.Join(
        '|',
        Text(item, "title"),
        Text(item.GetProperty("location"), "path"),
        item.GetProperty("location").GetProperty("startLine").ToString(),
        Text(item, "trigger"),
        Text(item, "consequence"),
        Text(item, "recommendation"),
        Text(item, "priority"));

    private static void AppendNotes(
        string section,
        IReadOnlyList<AcceptedResult> accepted,
        FocusedReviewTarget target,
        StringBuilder builder,
        bool inboxLinks,
        string absent)
    {
        var count = 0;
        foreach (var result in accepted)
        {
            foreach (var note in result.Data.GetProperty(section).EnumerateArray())
            {
                builder.Append("- ").Append(Escape(Text(note, "text"))).Append(" (").Append(result.Role).Append(
                    "). ")
                    .Append(Citations(note, target, inboxLinks)).Append('\n');
                count++;
            }
        }

        if (count == 0)
        {
            builder.Append(absent).Append('\n');
        }
    }

    private static string Text(JsonElement item, string name) => item.GetProperty(name).GetString() ?? string.Empty;

    private static string Citations(
        JsonElement item,
        FocusedReviewTarget target,
        bool inboxLinks)
        => string.Join(", ", item.GetProperty("evidence").EnumerateArray().Select(citation => Citation(citation, target, inboxLinks)));

    private static string Citation(
        JsonElement location,
        FocusedReviewTarget target,
        bool inboxLinks)
    {
        var path = Text(location, "path");
        var line = location.GetProperty("startLine").GetInt32();
        var end = location.GetProperty("endLine").GetInt32();
        var label = Escape($"{path}:L{line}-L{end}");
        var file = target.Files.Single(item => item.Path == path);
        if (file.Deleted)
        {
            return label + " (deleted source at captured merge base)";
        }

        var encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        if (target.Mode == "remoteBranch")
        {
            if (Uri.TryCreate(target.Repository, UriKind.Absolute, out var uri) && uri.Host == "github.com"
                && uri.AbsolutePath.Trim('/').Split('/').Length == 2)
            {
                var repo = uri.AbsolutePath.TrimEnd('/');
                if (repo.EndsWith(".git", StringComparison.Ordinal))
                {
                    repo = repo[..^4];
                }

                return $"[{label}](https://github.com{repo}/blob/{target.Revision}/{encoded}#L{line})";
            }

            return label + " (revision " + Escape(target.Revision) + ")";
        }

        return $"[{label}]({(inboxLinks ? "../" : string.Empty)}{encoded}#L{line})";
    }

    private static string Escape(
        string value)
    {
        var builder = new StringBuilder();
        foreach (var character in value)
        {
            if (char.IsControl(character))
            {
                builder.Append(' ');
                continue;
            }

            switch (character)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                case '|':
                    builder.Append("&#124;");
                    break;
                case '\\':
                case '`':
                case '*':
                case '_':
                case '[':
                case ']':
                case '#':
                case '!':
                    builder.Append('\\').Append(character);
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString();
    }

    private sealed record AcceptedResult(AgentRole Role, JsonElement Data);

    private sealed record Issue(AgentRole Role, JsonElement Data);
}
