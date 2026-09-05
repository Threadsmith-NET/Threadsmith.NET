namespace Threadsmith.Milestone12.Tests;

using Threadsmith.Core;
using Threadsmith.Skills;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies the focused packaged-documentation scope and citation boundary.</summary>
public sealed class Plan79DocumentationHelpTests
{
    /// <summary>The documentation selector binds existing file tools to an application-owned read-only root.</summary>
    [Fact]
    public static void BindToBundle_UsesOnlyPackagedDocumentationRoot()
    {
        var context = new ToolInvocationContext
        {
            WorkspaceId = WorkspaceId.New(),
            RepositoryPath = Path.GetTempPath(),
            TrustLevel = RepositoryTrustLevel.TrustedRead,
            AllowedExecutables = ["pwsh"],
            AllowedNetworkHosts = ["example.com"],
            AllowedSecretReferences = ["token"],
            RequestedBy = "test",
        };

        var bound = PackagedDocumentationPolicy.BindToBundle(context, AppContext.BaseDirectory);

        Assert.Null(bound.WorkspaceId);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "ThreadsmithDocs")),
            bound.RepositoryPath);
        Assert.Empty(bound.AllowedExecutables);
        Assert.Empty(bound.AllowedNetworkHosts);
        Assert.Empty(bound.AllowedSecretReferences);
        Assert.True(PackagedDocumentationPolicy.IsDocumentationSkillSelector(
            "Maintained:threadsmith-docs-help@1.0.0+sha256"));
    }

    /// <summary>Cited answers must identify exact snippets inside confined shipped documentation.</summary>
    [Fact]
    public static async Task ValidateAnswerAsync_RequiresExactConfinedCitation()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-docs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "guide.md"),
                "# Guide\nUse /compact to compact the current conversation.\n");
            var excludedDirectory = Path.Combine(root, "docs", "implementation-plans");
            Directory.CreateDirectory(excludedDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(excludedDirectory, "guide.md"),
                "# Guide\nUse /compact to compact the current conversation.\n");
            var valid = """
                {"status":"answered","answer":"Use /compact.","citations":[{"path":"guide.md","heading":"# Guide","lineStart":1,"lineEnd":2,"snippet":"Use /compact"}],"gaps":[]}
                """;

            await PackagedDocumentationPolicy.ValidateAnswerAsync(valid, root);

            var escaping = valid.Replace("guide.md", "../guide.md", StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackagedDocumentationPolicy.ValidateAnswerAsync(escaping, root));

            var wrongHeading = valid.Replace("# Guide", "# Other", StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackagedDocumentationPolicy.ValidateAnswerAsync(wrongHeading, root));

            var excluded = valid.Replace(
                "guide.md",
                "docs/implementation-plans/guide.md",
                StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PackagedDocumentationPolicy.ValidateAnswerAsync(excluded, root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
