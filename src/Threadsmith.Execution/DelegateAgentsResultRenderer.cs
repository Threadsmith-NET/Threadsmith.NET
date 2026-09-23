namespace Threadsmith.Execution;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;

/// <summary>Renders complete model-visible detail blocks without a separate character cap.</summary>
internal sealed class DelegateAgentsResultRenderer
{
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultRenderer"/> class.</summary>
    public DelegateAgentsResultRenderer(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
    }

    /// <summary>Creates a compact projection without cutting fields or child status lines.</summary>
    public string Render(DelegateAgentsResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder();
        builder.Append(
            _prompts.Render(
                PromptFileNames.ToolDelegateAgentsResultHeader,
                Tokens(
                    ("DelegationId", $"{result.DelegationId}"),
                    ("Status", $"{result.Status}"))));
        foreach (var child in result.Children)
        {
            builder.Append(
                _prompts.Render(
                    PromptFileNames.ToolDelegateAgentsChildStatus,
                    Tokens(
                        ("AssignmentId", $"{child.AssignmentId}"),
                        ("Role", $"{child.Role}"),
                        ("ToolAccess", $"{child.ToolAccess}"),
                        ("Status", $"{child.Status}"))));
        }

        foreach (var child in result.Children)
        {
            var summaryBlock = _prompts.Render(
                PromptFileNames.ToolDelegateAgentsChildSummary,
                Tokens(
                    ("AssignmentId", $"{child.AssignmentId}"),
                    ("Summary", child.Summary),
                    ("ModelTokens", $"{child.Usage.ModelTokens}"),
                    ("ToolCalls", $"{child.Usage.ToolCalls}")));
            builder.Append(summaryBlock);
            if (child.ModelSelection is not null || child.Implementation is not null)
            {
                var details = JsonSerializer.Serialize(new
                {
                    modelSelection = child.ModelSelection,
                    implementation = child.Implementation,
                });
                builder.Append(_prompts.Render(
                    PromptFileNames.ToolDelegateAgentsChildDetails,
                    Tokens(
                        ("AssignmentId", child.AssignmentId),
                        ("DetailsJson", details))));
            }
        }

        var maximumFindings = result.Children.Max(child => child.Findings.Count);
        for (var index = 0; index < maximumFindings; index++)
        {
            foreach (var child in result.Children.Where(item => index < item.Findings.Count))
            {
                builder.Append(RenderFinding(child, child.Findings[index]));
                var finding = child.Findings[index];
                if (finding.Severity is not null)
                {
                    var details = JsonSerializer.Serialize(new
                    {
                        finding.Title,
                        finding.Category,
                        finding.Severity,
                        finding.Line,
                        finding.Consequence,
                        finding.Recommendation,
                    });
                    builder.Append(_prompts.Render(
                        PromptFileNames.ToolDelegateAgentsReviewDetails,
                        Tokens(
                            ("AssignmentId", child.AssignmentId),
                            ("DetailsJson", details))));
                }
            }
        }

        foreach (var child in result.Children)
        {
            foreach (var omission in child.Omissions)
            {
                var block = _prompts.Render(
                    PromptFileNames.ToolDelegateAgentsChildOmission,
                    Tokens(("AssignmentId", $"{child.AssignmentId}"), ("Omission", omission)));
                builder.Append(block);
            }
        }

        foreach (var disagreement in result.Disagreements)
        {
            var block = _prompts.Render(
                PromptFileNames.ToolDelegateAgentsDisagreement,
                Tokens(("Disagreement", disagreement)));
            builder.Append(block);
        }

        foreach (var omission in result.Omissions)
        {
            var block = _prompts.Render(
                PromptFileNames.ToolDelegateAgentsDelegationOmission,
                Tokens(("Omission", omission)));
            builder.Append(block);
        }

        var steeringBlock = _prompts.Render(
            PromptFileNames.ToolDelegateAgentsSteering,
            Tokens(
                ("Submitted", $"{result.Steering.Submitted}"),
                ("Delivered", $"{result.Steering.Delivered}"),
                ("Undelivered", $"{result.Steering.Undelivered}")));
        builder.Append(steeringBlock);

        return builder.ToString().TrimEnd();
    }

    private string RenderFinding(
        DelegateAgentOutcomeSummary child,
        DelegateAgentFindingSummary finding)
    {
        var uncertaintyBlock = finding.Uncertainty is null
            ? string.Empty
            : PromptAssetRenderer.RenderWithPlatformLineEndings(
                _prompts,
                PromptFileNames.ToolDelegateAgentsFindingUncertainty,
                Tokens(("Uncertainty", finding.Uncertainty)));
        return PromptAssetRenderer.RenderWithPlatformLineEndings(
            _prompts,
            PromptFileNames.ToolDelegateAgentsFinding,
            Tokens(
                ("AssignmentId", $"{child.AssignmentId}"),
                ("Title", finding.Title),
                ("FilePathBlock", finding.FilePath is null ? string.Empty : $" [{finding.FilePath}]"),
                ("SymbolBlock", finding.Symbol is null ? string.Empty : $" symbol={finding.Symbol}"),
                ("Evidence", finding.Evidence),
                ("Confidence", $"{finding.Confidence}"),
                ("UncertaintyBlock", uncertaintyBlock)));
    }

    private static IReadOnlyDictionary<string, string> Tokens(
        params (string Name, string Value)[] values)
    {
        return values.ToDictionary(value => value.Name, value => value.Value, StringComparer.Ordinal);
    }
}
