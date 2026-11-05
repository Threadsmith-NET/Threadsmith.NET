namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Repository aliases retain the same prohibition and output paths as canonical spelling.</summary>
public sealed class ToolPathRulesTests
{
    /// <summary>Exercises normalization, ordinary listing, and shared reads on a case-insensitive filesystem.</summary>
    [Fact]
    public async Task RepositoryAliasCannotBypassProhibitions()
    {
        var temporary = Directory.CreateTempSubdirectory("threadsmith-path-alias-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(temporary, "Repo")).FullName;
            var alias = Path.Combine(temporary, "repo");
            Assert.SkipUnless(Directory.Exists(alias), "Requires a case-insensitive filesystem.");
            var secret = Directory.CreateDirectory(Path.Combine(root, "secret")).FullName;
            await File.WriteAllTextAsync(Path.Combine(secret, "file.txt"), "private", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "public.txt"), "public", TestContext.Current.CancellationToken);
            var context = new ToolInvocationContext
            {
                RepositoryPath = root,
                RequestedBy = "test",
                ApprovedRoots = ["."],
                ProhibitedPaths = ["secret/**"],
            };
            Assert.Equal(root, ToolPathRules.NormalizeAndValidate(alias, context));
            Assert.Equal(Path.Combine(root, "public.txt"), ToolPathRules.NormalizeAndValidate(Path.Combine(alias, "public.txt"), context));
            var prohibited = Path.Combine(alias, "secret", "file.txt");
            Assert.Throws<UnauthorizedAccessException>(() => ToolPathRules.NormalizeAndValidate(prohibited, context));
            Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.ValidateRead(prohibited, root, root, [root], context.ProhibitedPaths));
            var execution = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), context);
            var listing = await new ListFilesTool(TestPromptLoader.Instance).ExecuteAsync(new ListFilesInput { Path = alias }, execution, TestContext.Current.CancellationToken);
            Assert.Equal("public.txt", Assert.Single(listing.Value.Files).Path);
        }
        finally
        {
            Directory.Delete(temporary, recursive: true);
        }
    }
}
