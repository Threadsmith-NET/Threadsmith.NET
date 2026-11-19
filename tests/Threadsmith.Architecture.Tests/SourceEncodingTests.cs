namespace Threadsmith.Architecture.Tests;

using System.IO.Enumeration;
using System.Text;
using Xunit;

/// <summary>Ensures repository source can be decoded by the semantic workspace on every platform.</summary>
public static class SourceEncodingTests
{
    private static readonly string[] SourceDirectories = ["src", "tests", "samples"];

    /// <summary>Rejects legacy code-page bytes that can compile locally but fail semantic loading.</summary>
    [Fact]
    public static async Task CSharpSourcesAreValidUtf8()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var invalidFiles = new List<string>();
        foreach (var directory in SourceDirectories)
        {
            foreach (var file in EnumerateCSharpSources(Path.Combine(repositoryRoot, directory), cancellationToken))
            {
                var bytes = await File.ReadAllBytesAsync(file, cancellationToken);
                try
                {
                    _ = encoding.GetCharCount(bytes);
                }
                catch (DecoderFallbackException)
                {
                    invalidFiles.Add(Path.GetRelativePath(repositoryRoot, file));
                }
            }
        }

        Assert.True(invalidFiles.Count == 0, "C# sources must be UTF-8: " + string.Join(", ", invalidFiles));
    }

    internal static IEnumerable<string> EnumerateCSharpSources(string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = new FileSystemEnumerable<string>(
            directory,
            static (ref FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint,
            })
        {
            ShouldRecursePredicate = static (ref FileSystemEntry entry) =>
                !entry.FileName.Equals("bin", StringComparison.OrdinalIgnoreCase)
                && !entry.FileName.Equals("obj", StringComparison.OrdinalIgnoreCase),
            ShouldIncludePredicate = static (ref FileSystemEntry entry) =>
                !entry.IsDirectory && entry.FileName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase),
        };

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }
}
