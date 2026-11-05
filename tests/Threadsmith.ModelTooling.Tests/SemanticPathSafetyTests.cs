namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.DotNet;
using Threadsmith.Tools;
using Xunit;

/// <summary>Regression coverage for shared path authority contracts.</summary>
public sealed class SemanticPathSafetyTests
{
    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public void MissingSemanticInputsArePermittedButOutsidePathsFailClosed()
    {
        var root = Directory.CreateTempSubdirectory("threadsmith-semantic-path-").FullName;
        try
        {
            Assert.False(SemanticPathSafety.HasReparseComponent(root, Path.Combine(root, "missing.cs")));
            Assert.True(SemanticPathSafety.HasReparseComponent(root, root + "-sibling"));
            Assert.True(SemanticPathSafety.HasReparseComponent(root, "\0"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Captured PR paths need neither a live checkout nor live case probes.</summary>
    [Fact]
    public void CapturedPathsUseConservativeLexicalAuthority()
    {
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-absent-" + Guid.NewGuid().ToString("N"));
        var context = new ToolInvocationContext
        {
            RepositoryPath = root,
            RequestedBy = "test",
            ApprovedRoots = ["src"],
            ProhibitedPaths = ["src/secret/**"],
        };
        Assert.Equal(Path.Combine(root, "src", "File.cs"), ToolPathRules.NormalizeAndValidate("src/File.cs", context, inspectFileSystem: false));
        Assert.Throws<UnauthorizedAccessException>(() => ToolPathRules.NormalizeAndValidate("SRC/File.cs", context, inspectFileSystem: false));
        Assert.Throws<UnauthorizedAccessException>(() => ToolPathRules.NormalizeAndValidate("src/SECRET/File.cs", context, inspectFileSystem: false));
    }
}
