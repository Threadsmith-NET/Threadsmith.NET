namespace Threadsmith.Architecture.Tests;

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
        var pending = new Stack<string>(SourceDirectories
            .Select(directory => Path.Combine(repositoryRoot, directory)));
        var invalidFiles = new List<string>();
        while (pending.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(entry);
                    if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase)
                        && !name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        pending.Push(entry);
                    }
                }
                else if (entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    var bytes = await File.ReadAllBytesAsync(entry, cancellationToken);
                    try
                    {
                        _ = encoding.GetCharCount(bytes);
                    }
                    catch (DecoderFallbackException)
                    {
                        invalidFiles.Add(Path.GetRelativePath(repositoryRoot, entry));
                    }
                }
            }
        }

        Assert.True(invalidFiles.Count == 0, "C# sources must be UTF-8: " + string.Join(", ", invalidFiles));
    }
}
