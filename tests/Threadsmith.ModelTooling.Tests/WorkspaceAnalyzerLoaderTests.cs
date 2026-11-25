namespace Threadsmith.ModelTooling.Tests;

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Exercises analyzer assembly identity across independent workspace loaders.</summary>
public static class WorkspaceAnalyzerLoaderTests
{
    /// <summary>A refresh shares unchanged binaries and isolates same-identity rebuilds from active work.</summary>
    [Fact]
    public static void RefreshReusesUnchangedImagesAndRebindsChangedDependencies()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            var token = TestContext.Current.CancellationToken;
            var helper = Path.Combine(directory.CreateSubdirectory("dependency").FullName, "Helper.dll");
            var analyzer = Path.Combine(directory.CreateSubdirectory("analyzer").FullName, "Probe.dll");
            var independent = Path.Combine(directory.CreateSubdirectory("independent").FullName, "Other.dll");
            Emit(helper, "public static class Helper { public static int Value => 1; }", token);
            Emit(analyzer, "public static class Rule { public static int Value => Helper.Value; }", token, helper);
            Emit(independent, "public static class Other {}", token);

            var cache = new WorkspaceAnalyzerLoader.ContextCache();
            WorkspaceAnalyzerLoader Open()
            {
                var loader = new WorkspaceAnalyzerLoader(cache);
                foreach (var path in new[] { analyzer, independent, helper })
                {
                    loader.AddDependencyLocation(path);
                }

                return loader;
            }

            var original = Open();
            var first = original.LoadFromPath(analyzer);
            var other = original.LoadFromPath(independent);
            Assert.Equal(1, RuleValue(first));

            // An identical rebuild can replace the file while both workspaces use its assembly.
            var bytes = File.ReadAllBytes(analyzer);
            File.WriteAllBytes(analyzer, bytes);
            Assert.Same(first, Open().LoadFromPath(analyzer));

            // Preserve identity and timestamp so only the changed dependency bytes distinguish these builds.
            var timestamp = File.GetLastWriteTimeUtc(helper);
            Emit(helper, "public static class Helper { public static int Value => 2; }", token);
            File.SetLastWriteTimeUtc(helper, timestamp);
            var refreshed = Open();
            var replacement = refreshed.LoadFromPath(analyzer);
            Assert.NotSame(first, replacement);
            Assert.Equal(2, RuleValue(replacement));
            Assert.Equal(1, RuleValue(first));
            Assert.Same(other, refreshed.LoadFromPath(independent));

            // Another workspace can register a different helper with the same assembly name.
            var alternate = Path.Combine(directory.CreateSubdirectory("alternate").FullName, "Helper.dll");
            Emit(alternate, "public static class Helper { public static int Value => 4; }", token);
            var separate = new WorkspaceAnalyzerLoader(cache);
            separate.AddDependencyLocation(alternate);
            Assert.Equal(4, RuleValue(separate.LoadFromPath(analyzer)));
            Assert.Equal(2, RuleValue(replacement));

            Emit(analyzer, "public static class Rule { public static int Value => 3; }", token);
            Assert.Equal(3, RuleValue(Open().LoadFromPath(analyzer)));
            Assert.Equal(2, RuleValue(refreshed.LoadFromPath(analyzer)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static int RuleValue(Assembly assembly) =>
        (int)assembly.GetType("Rule", throwOnError: true)!.GetProperty("Value")!.GetValue(null)!;

    private static void Emit(string path, string source, CancellationToken token, string? dependency = null)
    {
        var references = new List<MetadataReference> { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        if (dependency is not null)
        {
            references.Add(MetadataReference.CreateFromImage(File.ReadAllBytes(dependency)));
        }

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(path),
            [CSharpSyntaxTree.ParseText(source, cancellationToken: token)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var output = File.Create(path);
        var result = compilation.Emit(output, cancellationToken: token);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
    }
}
