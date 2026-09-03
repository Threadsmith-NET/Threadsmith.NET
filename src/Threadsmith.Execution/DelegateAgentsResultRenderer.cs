namespace Threadsmith.Execution;

using System.Globalization;
using System.Text;
using Threadsmith.Core;

/// <summary>Renders complete model-visible detail blocks within a fixed character budget.</summary>
internal sealed class DelegateAgentsResultRenderer
{
    private const int MaximumModelProjectionCharacters = 48 * 1024;
    private readonly IPromptLoader _prompts;

    /// <summary>Initializes a new instance of the <see cref="DelegateAgentsResultRenderer"/> class.</summary>
    public DelegateAgentsResultRenderer(IPromptLoader prompts)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        _prompts = prompts;
    }

    /// <summary>Creates a compact projection without cutting fields or child status lines.</summary>
    public string Render(DelegateAgentsResult result, out bool truncated)
    {
        ArgumentNullException.ThrowIfNull(result);
        var builder = new StringBuilder(MaximumModelProjectionCharacters);
        var maximumOmittedBlocks = checked(
            2
            + (result.Children.Count * 2)
            + result.Children.Sum(child => child.Findings.Count + child.Omissions.Count)
            + result.Disagreements.Count
            + result.Omissions.Count);
        var maximumFooter = RenderTruncationFooter(maximumOmittedBlocks);
        if (maximumFooter.Length > MaximumModelProjectionCharacters)
        {
            throw new InvalidOperationException(
                "The delegate_agents truncation prompt exceeds the model projection bound.");
        }

        var detailLimit = MaximumModelProjectionCharacters - maximumFooter.Length;
        var omittedBlocks = 0;
        AppendCompleteBlockOrCountOmission(
            _prompts.Render(
                PromptFileNames.ToolDelegateAgentsResultHeader,
                Tokens(
                    ("DelegationId", $"{result.DelegationId}"),
                    ("Status", $"{result.Status}"))));
        foreach (var child in result.Children)
        {
            AppendCompleteBlockOrCountOmission(
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
            AppendCompleteBlockOrCountOmission(summaryBlock);
        }

        var maximumFindings = result.Children.Max(child => child.Findings.Count);
        for (var index = 0; index < maximumFindings; index++)
        {
            foreach (var child in result.Children.Where(item => index < item.Findings.Count))
            {
                AppendCompleteBlockOrCountOmission(RenderFinding(child, child.Findings[index]));
            }
        }

        foreach (var child in result.Children)
        {
            foreach (var omission in child.Omissions)
            {
                var block = _prompts.Render(
                    PromptFileNames.ToolDelegateAgentsChildOmission,
                    Tokens(("AssignmentId", $"{child.AssignmentId}"), ("Omission", omission)));
                AppendCompleteBlockOrCountOmission(block);
            }
        }

        foreach (var disagreement in result.Disagreements)
        {
            var block = _prompts.Render(
                PromptFileNames.ToolDelegateAgentsDisagreement,
                Tokens(("Disagreement", disagreement)));
            AppendCompleteBlockOrCountOmission(block);
        }

        foreach (var omission in result.Omissions)
        {
            var block = _prompts.Render(
                PromptFileNames.ToolDelegateAgentsDelegationOmission,
                Tokens(("Omission", omission)));
            AppendCompleteBlockOrCountOmission(block);
        }

        var steeringBlock = _prompts.Render(
            PromptFileNames.ToolDelegateAgentsSteering,
            Tokens(
                ("Submitted", $"{result.Steering.Submitted}"),
                ("Delivered", $"{result.Steering.Delivered}"),
                ("Undelivered", $"{result.Steering.Undelivered}")));
        AppendCompleteBlockOrCountOmission(steeringBlock);

        truncated = omittedBlocks > 0;
        if (truncated)
        {
            var footer = RenderTruncationFooter(omittedBlocks);
            if (!TryAppend(builder, footer))
            {
                throw new InvalidOperationException(
                    "The complete delegate_agents truncation footer does not fit its reserved projection space.");
            }
        }

        return builder.ToString().TrimEnd();

        void AppendCompleteBlockOrCountOmission(string block)
        {
            if (!TryAppend(builder, block, detailLimit))
            {
                omittedBlocks++;
            }
        }
    }

    private string RenderTruncationFooter(int omittedBlocks)
    {
        return PromptAssetRenderer.RenderWithPlatformLineEndings(
            _prompts,
            PromptFileNames.ToolDelegateAgentsTruncation,
            Tokens(("OmittedBlockCount", omittedBlocks.ToString(CultureInfo.InvariantCulture))));
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

    private static bool TryAppend(
        StringBuilder builder,
        string block,
        int maximumCharacters = MaximumModelProjectionCharacters)
    {
        if (builder.Length + block.Length > maximumCharacters)
        {
            return false;
        }

        builder.Append(block);
        return true;
    }
}
