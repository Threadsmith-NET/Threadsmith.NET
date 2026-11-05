namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Xunit;

/// <summary>Regression coverage for shared path authority contracts.</summary>
public sealed class RepositoryPathPolicyTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("threadsmith-path-policy-").FullName;

    /// <summary>Case aliases use authority spelling even where the runtime's relative-path rules are ordinal.</summary>
    [Fact]
    public void NormalizedContainedAliasesRemainRepositoryRelative()
    {
        var root = Path.Combine(_root, "Repo");
        var alias = Path.Combine(_root, "repo", "secret", "file.txt");
        var normalized = RepositoryPathPolicy.NormalizeContainedPath(root, alias, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "secret", "file.txt"), normalized);
        Assert.Equal(Path.Combine("secret", "file.txt"), Path.GetRelativePath(root, normalized));
        Assert.Equal(root, RepositoryPathPolicy.NormalizeContainedPath(root, Path.Combine(_root, "repo"), StringComparer.OrdinalIgnoreCase));
        Assert.Throws<UnauthorizedAccessException>(() => RepositoryPathPolicy.NormalizeContainedPath(root, root + "-sibling", StringComparer.OrdinalIgnoreCase));
        var volume = Path.GetPathRoot(_root)!;
        Assert.Equal(_root, RepositoryPathPolicy.NormalizeContainedPath(volume, _root));
    }

    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public void ContainmentAndIntersectionDoNotGrantSiblingPrefixes()
    {
        var child = Path.Combine(_root, "child");
        var sibling = Path.Combine(_root, "child-other");
        Assert.True(RepositoryPathPolicy.ContainsPath(_root, child));
        Assert.True(RepositoryPathPolicy.PathsEqual(_root, _root + Path.DirectorySeparatorChar));
        Assert.False(RepositoryPathPolicy.ContainsPath(child, sibling));
        Assert.Equal([child], RepositoryPathPolicy.IntersectRoots([_root, child], [child]));
        Assert.Equal([child], RepositoryPathPolicy.IntersectRoots([child], [_root]));
        Assert.Empty(RepositoryPathPolicy.IntersectRoots([child], [sibling]));
        Assert.Empty(RepositoryPathPolicy.IntersectRoots([], [child]));
        Assert.Empty(RepositoryPathPolicy.IntersectRoots([child], []));
    }

    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public void FilesystemRootAndExplicitComparersPreserveBoundaries()
    {
        var volume = Path.GetPathRoot(_root)!;
        Assert.True(RepositoryPathPolicy.ContainsPath(volume, _root));
        if (OperatingSystem.IsWindows())
        {
            Assert.True(RepositoryPathPolicy.ContainsPath(@"\\server\share\", @"\\server\share\file", StringComparer.OrdinalIgnoreCase));
            Assert.False(RepositoryPathPolicy.ContainsPath(@"\\server\share\", @"\\server\share-other\file", StringComparer.OrdinalIgnoreCase));
        }

        var empty = Directory.CreateDirectory(Path.Combine(_root, "empty")).FullName;
        Assert.Empty(RepositoryPathPolicy.IntersectRoots([Path.Combine(empty, "grant")], [Path.Combine(empty, "GRANT")]));
        var lower = Path.Combine(_root, "lower");
        var upper = Path.Combine(_root, "LOWER");
        Assert.False(RepositoryPathPolicy.ContainsPath(lower, upper, StringComparer.Ordinal));
        Assert.True(RepositoryPathPolicy.ContainsPath(lower, upper, StringComparer.OrdinalIgnoreCase));
        Directory.CreateDirectory(lower);
        File.WriteAllText(Path.Combine(lower, "CaseProbe.txt"), "probe");
        var sensitive = !Directory.Exists(upper);
        Assert.Equal(!sensitive, RepositoryPathPolicy.ContainsPath(lower, upper));
        if (sensitive)
        {
            Directory.CreateDirectory(upper);
            Assert.Empty(RepositoryPathPolicy.IntersectRoots([lower], [upper]));
        }
    }

    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public void MissingTargetsAreAllowedOnlyForWriteInspection()
    {
        var missing = Path.Combine(_root, "missing", "file.txt");
        ScopedFilePaths.RejectLinks(missing, _root);
        Assert.ThrowsAny<IOException>(() => ScopedFilePaths.RejectLinks(missing, _root, allowMissing: false));
        Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.RejectLinks(_root + "-sibling", _root));
        Assert.True(RepositoryPathPolicy.IsProhibited("SECRET/File.txt", ["secret/**"], missing, null, inspectFileSystem: false));
    }

    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public void LinkBoundaryIncludesRootAndTargetButNotTrustedAncestors()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        var repository = Directory.CreateDirectory(Path.Combine(target, "repo")).FullName;
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Symbolic links unavailable: {exception.Message}");
        }

        Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.RejectLinks(Path.Combine(link, "repo", "missing"), link));
        Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.RejectLinks(link, _root));
        ScopedFilePaths.RejectLinks(Path.Combine(link, "repo", "missing"), Path.Combine(link, "repo"));
        var dangling = Path.Combine(repository, "dangling");
        File.CreateSymbolicLink(dangling, Path.Combine(_root, "absent"));
        Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.RejectLinks(dangling, repository));
        var danglingDirectory = Path.Combine(repository, "dangling-directory");
        Directory.CreateSymbolicLink(danglingDirectory, Path.Combine(_root, "absent-directory"));
        Assert.Throws<UnauthorizedAccessException>(() => ScopedFilePaths.RejectLinks(Path.Combine(danglingDirectory, "missing", "file.txt"), repository));
    }

    /// <summary>Root deduplication removes only filesystem-equivalent duplicates.</summary>
    [Fact]
    public void DistinctRootsRemoveOnlyFilesystemEquivalents()
    {
        var lower = Path.Combine(_root, "lower");
        Directory.CreateDirectory(lower);
        File.WriteAllText(Path.Combine(lower, "CaseProbe.txt"), "probe");
        var upper = Path.Combine(_root, "LOWER");
        var sensitive = !Directory.Exists(upper);
        var distinct = Path.Combine(_root, "other");
        var roots = RepositoryPathPolicy.DistinctRoots([lower, upper, distinct, lower]);
        string[] expected = sensitive ? [lower, upper, distinct] : [lower, distinct];
        Assert.Equal(expected, roots);
    }

    /// <summary>Case aliases of the authority root stop the link walk at the root, not at trusted ancestors.</summary>
    [Fact]
    public void LinkWalkStopsAtCaseAliasAuthorityRoot()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        Directory.CreateDirectory(Path.Combine(target, "Repo"));
        var link = Path.Combine(_root, "link");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Skip($"Symbolic links unavailable: {exception.Message}");
        }

        var aliasRoot = Path.Combine(link, "repo");
        if (!Directory.Exists(aliasRoot))
        {
            // Case-sensitive filesystems do not admit the alias spelling.
            return;
        }

        // The symlink above the authority root must remain trusted; a walk that failed
        // to stop at the root would reject it.
        ScopedFilePaths.RejectLinks(Path.Combine(aliasRoot, "missing.txt"), aliasRoot);
    }

    /// <summary>Intersection probe work scales with governing directories rather than root pairs.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void IntersectionCachesCommonPrefixProbesAcrossAllPairs(bool? sensitive)
    {
        var probes = new List<string>();
        var context = new RepositoryPathCaseContext(_root, directory =>
        {
            probes.Add(Path.TrimEndingDirectorySeparator(directory));
            return sensitive;
        });
        var parent = Path.Combine(_root, "Repo");
        var alias = Path.Combine(_root, "repo");
        var parents = Enumerable.Range(0, 100).Select(index => Path.Combine(parent, $"p{index:D3}")).ToArray();
        var children = Enumerable.Range(0, 100).Select(index => Path.Combine(alias, $"p{index:D3}", "src")).ToArray();

        var result = RepositoryPathPolicy.IntersectRoots(parents.Concat(parents), children.Concat(children), context);

        Assert.Equal(sensitive is null ? [] : children, result);
        Assert.Equal([_root], probes);
    }

    /// <summary>Containment and prohibition resolution share the same raw filesystem evidence.</summary>
    [Fact]
    public void DeduplicationAndProhibitionsShareRawCaseEvidence()
    {
        var probes = 0;
        var context = new RepositoryPathCaseContext(_root, _ =>
        {
            probes++;
            return false;
        });
        var upper = Path.Combine(_root, "Repo");
        var lower = Path.Combine(_root, "repo");

        Assert.Equal([upper], RepositoryPathPolicy.IntersectRoots([upper, lower], [upper, lower], context));
        Assert.True(RepositoryPathPolicy.IsProhibited("Repo/file.txt", ["repo/**"], _root, context));
        Assert.Equal(1, probes);
    }

    /// <summary>Missing suffixes require only one failing lookup.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinkInspectionStopsAtFirstMissingComponent(bool allowMissing)
    {
        var inspected = new List<string>();
        var missing = Path.Combine(_root, "missing");
        var target = Path.Combine(missing, "one", "two", "three", "file.txt");
        FileAttributes ReadAttributes(string path)
        {
            inspected.Add(path);
            return path == _root ? FileAttributes.Directory : throw new DirectoryNotFoundException();
        }

        if (allowMissing)
        {
            ScopedFilePaths.RejectLinks(target, _root, true, null, ReadAttributes);
        }
        else
        {
            Assert.Throws<DirectoryNotFoundException>(() => ScopedFilePaths.RejectLinks(target, _root, false, null, ReadAttributes));
        }

        Assert.Equal([_root, missing], inspected);
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_root, recursive: true);
}
