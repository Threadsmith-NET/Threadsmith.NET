namespace Threadsmith.Tools;

using System.Globalization;
using Threadsmith.Core;

/// <summary>Shared exact-call rejection metadata from turn admission.</summary>
/// <param name="IsDuplicateSibling">Whether the same exact request occurs twice in this response.</param>
/// <param name="InvocationCount">Number of accepted exact invocations before this rejection.</param>
public sealed record ToolCallRejection(bool IsDuplicateSibling, int InvocationCount)
{
    /// <summary>Renders the cataloged correction without exposing tool arguments.</summary>
    public string Render(IPromptLoader prompts, string toolName)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal) { ["ToolName"] = toolName };
        if (!IsDuplicateSibling)
        {
            tokens["InvocationCount"] = InvocationCount.ToString(CultureInfo.InvariantCulture);
        }

        return prompts.Render(
            IsDuplicateSibling ? PromptFileNames.CorrectionToolDuplicateSiblingInvocation : PromptFileNames.CorrectionToolDuplicateInvocation,
            tokens);
    }
}
