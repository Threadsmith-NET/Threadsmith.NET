namespace Threadsmith.Skills;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Tools;

/// <summary>Publishes only complete UTF-8 report artifacts into an existing authorized invoking-repository inbox.</summary>
public sealed class FocusedReviewReportWriter
{
    /// <summary>Returns an existing authorized inbox, absence for console, or an honest destination error.</summary>
    public static string? ResolveInbox(
        FocusedReviewTarget target,
        ToolInvocationContext authority)
    {
        if (target.InvokingRepository is null)
        {
            return null;
        }

        if (!Path.GetFullPath(authority.RepositoryPath).Equals(
            target.InvokingRepository,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("The invoking report repository changed.");
        }

        var path = ReviewPathAccess.Resolve(".inbox", authority);
        if (File.Exists(path))
        {
            throw new IOException("The repository .inbox path is a file, not a directory.");
        }

        return Directory.Exists(path) ? path : null;
    }

    /// <summary>Reconciles a previously persisted report name, never replacing a prior artifact.</summary>
    public static async Task PublishAsync(
        string savedPath,
        string markdown,
        string digest,
        FocusedReviewTarget target,
        ToolInvocationContext authority,
        CancellationToken cancellationToken = default)
    {
        var inbox = ResolveInbox(target, authority) ?? throw new IOException("The selected inbox is no longer present.");
        var validatedPath = ReviewPathAccess.Resolve(savedPath, authority);
        if (Path.GetDirectoryName(validatedPath) != inbox || !Path.GetFileName(validatedPath).StartsWith("review-", StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("Invalid review report destination.");
        }

        var bytes = new UTF8Encoding(false, true).GetBytes(markdown);
        if (FocusedReviewTargetCapture.Hash(bytes) != digest)
        {
            throw new InvalidDataException("Report content identity changed before delivery.");
        }

        if (File.Exists(validatedPath))
        {
            if (FocusedReviewTargetCapture.Hash(await File.ReadAllBytesAsync(validatedPath, cancellationToken)) != digest)
            {
                throw new IOException("The report filename already exists with different content; it was not overwritten.");
            }

            return;
        }

        var temporary = Path.Combine(inbox, ".review-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                65536,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = ResolveInbox(target, authority);
            _ = ReviewPathAccess.Resolve(temporary, authority);
            _ = ReviewPathAccess.Resolve(validatedPath, authority);
            File.Move(temporary, validatedPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                _ = ReviewPathAccess.Resolve(temporary, authority);
                File.Delete(temporary);
            }
        }
    }
}
