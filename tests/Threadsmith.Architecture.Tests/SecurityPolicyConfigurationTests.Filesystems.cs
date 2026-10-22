namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Every literal component obeys its parent, even when nested case behavior changes in both directions.</summary>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProhibitedGlobsUseEachDirectoryCaseMode(bool rootSensitive, bool mountSensitive)
    {
        var mount = Path.Combine(_paths.RepositoryRoot, "Mount");
        Directory.CreateDirectory(mount);
        var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, directory =>
            directory == _paths.RepositoryRoot ? rootSensitive : mountSensitive);
        Assert.Equal(!rootSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["mount/File.txt"], _paths.RepositoryRoot, context));
        Assert.Equal(!mountSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["Mount/file.txt"], _paths.RepositoryRoot, context));
        Assert.Equal(!rootSensitive && !mountSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["mount/file.txt"], _paths.RepositoryRoot, context));
        Assert.Equal(!mountSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["**/file.txt"], _paths.RepositoryRoot, context));
        Assert.Equal(!mountSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["M*/f?le.*"], _paths.RepositoryRoot, context));
        Assert.Equal(!mountSensitive, RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["M**file.txt"], _paths.RepositoryRoot, context));
    }

    /// <summary>Case-aware wildcard matching explores alternate alignments rather than trusting one regex capture.</summary>
    [Fact]
    public void ProhibitedGlobRetainsAlternateWildcardAlignments()
    {
        Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, "Mount"));
        var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, directory => directory != _paths.RepositoryRoot);
        Assert.True(RepositoryPathPolicy.IsProhibited("Mount/aA.txt", ["mount/*A*"], _paths.RepositoryRoot, context));
        Assert.False(RepositoryPathPolicy.IsProhibited("Mount/aa.txt", ["mount/*A*"], _paths.RepositoryRoot, context));
    }

    /// <summary>Flat scans probe each relevant directory once and exact/unrelated paths need no probe.</summary>
    [Fact]
    public void DirectoryCaseProbesAreBoundedByDirectoriesRatherThanFiles()
    {
        var mount = Path.Combine(_paths.RepositoryRoot, "Mount");
        Directory.CreateDirectory(mount);
        var probes = new Dictionary<string, int>(StringComparer.Ordinal);
        var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, directory =>
        {
            probes[directory] = probes.GetValueOrDefault(directory) + 1;
            return false;
        });
        Assert.True(RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["Mount/**"], _paths.RepositoryRoot, context));
        Assert.False(RepositoryPathPolicy.IsProhibited("Mount/File.txt", ["Elsewhere/**"], _paths.RepositoryRoot, context));
        Assert.Empty(probes);
        for (var index = 0; index < 1000; index++)
        {
            Assert.True(RepositoryPathPolicy.IsProhibited($"Mount/File{index}.txt", ["mount/file*.txt"], _paths.RepositoryRoot, context));
        }

        Assert.Equal(2, probes.Count);
        Assert.All(probes.Values, count => Assert.Equal(1, count));
    }

    /// <summary>Case aliases retain segment-star, globstar and optional-directory semantics.</summary>
    [Theory]
    [InlineData("Mount/Sub/File.txt", "mount/**/file.txt", true)]
    [InlineData("Mount/File.txt", "mount/**/file.txt", true)]
    [InlineData("Mount/Sub/File.txt", "mount/*/file.txt", true)]
    [InlineData("Mount/File.txt", "mount/*/file.txt", false)]
    [InlineData("Mount/Sub/Deep/File.txt", "mount/*/file.txt", false)]
    [InlineData("Mount/Sub/Deep/File.txt", "mount/**file.txt", true)]
    [InlineData("Mount/Sub/Deep/File.txt", "mount/?ile.txt", false)]
    public void CaseAwareGlobsPreserveWildcardSemantics(string path, string pattern, bool expected)
    {
        var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, _ => false);
        Assert.Equal(expected, RepositoryPathPolicy.IsProhibited(path, [pattern], _paths.RepositoryRoot, context));
    }

    /// <summary>An empty mount cannot borrow a more permissive case rule from its parent.</summary>
    [Fact]
    public void UnknownDirectoryCaseCannotBypassProhibitions()
    {
        Directory.CreateDirectory(Path.Combine(_paths.RepositoryRoot, "Mount"));
        var context = new RepositoryPathCaseContext(_paths.RepositoryRoot, directory => directory == _paths.RepositoryRoot ? true : null);
        Assert.True(RepositoryPathPolicy.IsProhibited("Mount/FILE.txt", ["Mount/file.txt"], _paths.RepositoryRoot, context));
    }
}
