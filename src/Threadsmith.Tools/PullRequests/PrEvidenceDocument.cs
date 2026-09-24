namespace Threadsmith.Tools.PullRequests;

using System.Text;
using Threadsmith.Tools;

/// <summary>Formats one completed PR snapshot over the shared text-evidence reader.</summary>
internal sealed class PrEvidenceDocument
{
    private readonly TextEvidenceDocument _document;

    /// <summary>Initializes a new instance of the <see cref="PrEvidenceDocument"/> class without copying the captured diff.</summary>
    public PrEvidenceDocument(PrFetchOutput snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.Append("PR snapshot ").Append(snapshot.SnapshotId.ToString("D"))
            .Append(" (captured ").Append(snapshot.CapturedAt.ToString("O")).AppendLine(")");
        builder.Append("Provider: ").AppendLine(snapshot.Provider);
        builder.Append("Kind: ").AppendLine(snapshot.Kind.ToString());
        builder.Append("URL: ").AppendLine(snapshot.Metadata.Url);
        builder.Append("Repository: ").AppendLine(snapshot.Metadata.Repository);
        builder.Append("Number: ").AppendLine(snapshot.Metadata.Number);
        builder.Append("Title: ").AppendLine(snapshot.Metadata.Title);
        builder.Append("Description: ").AppendLine(snapshot.Metadata.Description);
        builder.Append("State: ").AppendLine(snapshot.Metadata.State);
        builder.Append("Source repository: ").AppendLine(snapshot.Metadata.SourceRepository);
        builder.Append("Source commit: ").AppendLine(snapshot.Metadata.SourceCommit);
        builder.Append("Destination repository: ").AppendLine(snapshot.Metadata.DestinationRepository);
        builder.Append("Destination commit: ").AppendLine(snapshot.Metadata.DestinationCommit);
        builder.Append("Revision: ").AppendLine(snapshot.Metadata.Revision);
        builder.Append("Expected files: ").AppendLine(snapshot.Metadata.ExpectedFiles?.ToString() ?? "unknown");
        builder.AppendLine("Changed files:");
        foreach (var file in snapshot.Page.Files)
        {
            builder.Append(file.Status).Append(' ').Append(file.Path);
            if (file.PreviousPath is not null)
            {
                builder.Append(" (previous: ").Append(file.PreviousPath).Append(')');
            }

            builder.AppendLine();
            if (file.Limitation is not null)
            {
                builder.Append("File limitation: ").AppendLine(file.Limitation);
            }
        }

        foreach (var limitation in snapshot.Page.Limitations)
        {
            builder.Append("Limitation: ").AppendLine(limitation);
        }

        builder.AppendLine("Diff:");
        _document = new TextEvidenceDocument(builder.ToString(), snapshot.Page.Diff);
    }

    /// <summary>Reads a bounded portion, including a column cursor for unusually long lines.</summary>
    public TextEvidenceReadResult Read(
        int startLine = 1,
        int? endLine = null,
        int startColumn = 1,
        int maximumCharacters = TextEvidenceDocument.MaximumReadCharacters)
        => _document.Read(startLine, endLine, startColumn, maximumCharacters);
}
