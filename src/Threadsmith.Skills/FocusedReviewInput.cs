namespace Threadsmith.Skills;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>Cross-field validation for the public review skill's three input modes.</summary>
public sealed record FocusedReviewInput
{
    /// <summary>Strict public-input and output serialization contract.</summary>
    internal static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>Current changes by default, remote branch, or special instructions.</summary>
    public string Mode { get; init; } = "currentBranchChanges";

    /// <summary>Optional comparison branch.</summary>
    public string? BaseBranch { get; init; }

    /// <summary>Exact remote URL or configured remote name.</summary>
    public string? Repository { get; init; }

    /// <summary>Exact requested remote branch.</summary>
    public string? Branch { get; init; }

    /// <summary>Explicit objective; never executable authority.</summary>
    public string Instructions { get; init; } = string.Empty;

    /// <summary>Optional repository-relative review scope.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Optional Markdown or plain-text acceptance document.</summary>
    public string? RequirementsDocumentPath { get; init; }

    /// <summary>Workspace by default or the frozen review target.</summary>
    public string RequirementsSource { get; init; } = "workspace";

    /// <summary>Rejects ambiguous input before acquisition or inference.</summary>
    public static FocusedReviewInput Parse(
        string json)
    {
        var input = JsonSerializer.Deserialize<FocusedReviewInput>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException("Review input must be an object.");
        if (input.Mode is not ("currentBranchChanges" or "remoteBranch" or "specialInstructions")
            || input.RequirementsSource is not ("workspace" or "reviewTarget")
            || input.Paths is null || input.Instructions is null)
        {
            throw new InvalidDataException("Unknown review mode, requirements source or invalid scope.");
        }

        if (input.Mode == "remoteBranch")
        {
            if (string.IsNullOrWhiteSpace(input.Repository) || string.IsNullOrWhiteSpace(input.Branch))
            {
                throw new InvalidDataException("Remote review requires repository and an exact branch.");
            }
        }
        else if (input.Repository is not null || input.Branch is not null)
        {
            throw new InvalidDataException("repository and branch require remoteBranch mode.");
        }

        if (input.Mode == "specialInstructions" && string.IsNullOrWhiteSpace(input.Instructions))
        {
            throw new InvalidDataException("Special-instructions review requires a nonempty objective.");
        }

        if (input.RequirementsDocumentPath is null && input.RequirementsSource != "workspace")
        {
            throw new InvalidDataException("requirementsSource requires a requirements document path.");
        }

        foreach (var path in input.Paths)
        {
            ValidateRelativePath(path);
        }

        return input;
    }

    /// <summary>Rejects ambiguous or escaping repository-relative scope paths.</summary>
    internal static void ValidateRelativePath(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(
            '\\')
            || path.Contains(':') || path.Any(
                char.IsControl)
            || path.Split('/').Any(part => part is ".." or "" or ".git"))
        {
            throw new InvalidDataException("Review scope must use confined repository-relative paths.");
        }
    }
}
