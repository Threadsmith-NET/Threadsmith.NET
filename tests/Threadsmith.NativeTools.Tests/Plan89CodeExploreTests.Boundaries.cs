namespace Threadsmith.NativeTools.Tests;

using System.Text;
using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

public sealed partial class Plan89CodeExploreTests
{
    /// <summary>A file whose first line cannot fit releases its source-bearing slot to another exact file.</summary>
    [Fact]
    public async Task CodeExplore_EmptyFittedSection_ReleasesFileSlot()
    {
        var files = new Dictionary<string, string> { ["A.cs"] = "// " + new string('a', 1000) + "\npublic class A { }\n" };
        await using var fixture = await AllocationFixture.CreateAsync(additionalFiles: files);
        var request = new CodeExploreRequest
        {
            Query = "requested source",
            PathAnchors = [new CodeExplorePathAnchor { Path = "A.cs" }, new CodeExplorePathAnchor { Path = "Small.cs" }],
            Limits = new CodeExploreLimits { MaximumFiles = 1, MaximumSourceCharacters = 100, MaximumPerFileSourceCharacters = 2000 },
        };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        var bearing = Assert.Single(result.FileSections, section => section.Source.NumberedLines.Count > 0);
        Assert.Equal("Small.cs", bearing.FilePath);
        Assert.Contains(result.ContinuationTargets, target => target.FilePath == "A.cs" && target.StartLine == 1);
        Assert.True(result.Allocation?.SpentSourceCharacters <= 100);
    }

    /// <summary>Completion cannot overrun an enabled per-file ceiling even with spare aggregate space.</summary>
    [Fact]
    public async Task CodeExplore_Completion_RespectsPerFileCap()
    {
        await using var fixture = await AllocationFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "Small.cs Large.cs",
            Limits = new CodeExploreLimits { MaximumFiles = 2, MaximumSourceCharacters = 5000, MaximumPerFileSourceCharacters = 1000 },
        };
        var result = (await fixture.Tool.ExecuteAsync(request, fixture.Context, TestContext.Current.CancellationToken)).Value;
        var large = Assert.Single(result.FileSections, section => section.FilePath == "Large.cs");
        Assert.Equal(CodeExploreSourceCompleteness.Partial, large.Source.Completeness);
        Assert.All(result.Allocation?.Files ?? [], file => Assert.True(file.SpentCharacters <= 1000));
        Assert.Contains(result.ContinuationTargets, target => target.FilePath == "Large.cs" && target.StartLine == large.Source.Range.EndLine + 1);
    }

    /// <summary>Raised source-read limits and disabled output caps reach both structured and Markdown consumers.</summary>
    [Fact]
    public async Task CodeExplore_RaisedReadSizeAndDisabledOutput_ReturnLargeCurrentSource()
    {
        var source = string.Join("\n", Enumerable.Repeat("// read boundary evidence", 44_000)) + "\npublic class Huge { public int Value => 1; }\n";
        Assert.True(Encoding.UTF8.GetByteCount(source) > 1024 * 1024);
        var options = new CodeExploreOptions
        {
            AdaptiveSizingEnabled = false,
            MaximumCurrentSourceFileBytes = 2_000_000,
            MaximumResultBytes = 0,
            MaximumMarkdownBytes = 0,
            Limits = new CodeExploreLimits { MaximumSourceCharacters = 2_000_000, MaximumPerFileSourceCharacters = 2_000_000 },
        };
        var files = new Dictionary<string, string> { ["Huge.cs"] = source };
        await using var fixture = await AllocationFixture.CreateAsync(options, files);
        var formatter = new CodeExploreOutputFormattingTool(fixture.Tool, new CodeExploreOutputOptions(), TestPromptLoader.Instance, options);
        var execution = await formatter.ExecuteAsync(new CodeExploreInput { Query = "Huge.cs" }, fixture.Context, TestContext.Current.CancellationToken);
        var result = Assert.IsType<CodeExploreResult>(execution.Value);
        var section = Assert.Single(result.FileSections);
        Assert.Equal(CodeExploreSourceCompleteness.Complete, section.Source.Completeness);
        Assert.True(Encoding.UTF8.GetByteCount(execution.ModelResultContent ?? string.Empty) > 1_000_000);
    }
}
