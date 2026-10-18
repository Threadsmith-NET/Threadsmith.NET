namespace Threadsmith.ParallelAgents.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Aggregate image admission reserves capacity across results without losing metadata.</summary>
public sealed class ToolImageAdmissionTests
{
    /// <summary>Count and byte boundaries use image occurrences, including repeated references.</summary>
    [Theory]
    [InlineData(8, 100, 100, 0)]
    [InlineData(7, 100, 100, 1)]
    [InlineData(4, 5242880, 100, 0)]
    [InlineData(3, 5242880, 5242880, 1)]
    [InlineData(3, 5242879, 5242880, 1)]
    public void ResultsReserveRemainingCapacity(int count, int reservedBytes, int incomingBytes, int accepted)
    {
        ImageArtifact Image(string id, int bytes) => new()
        {
            Id = id, ContentHash = new string('a', 64), MediaType = "image/png",
            Width = 2, Height = 2, ByteLength = bytes, Source = "fixture",
        };
        var reserved = Enumerable.Repeat(Image("current", reservedBytes), count).ToList();
        ToolInvocationResult Result(string id) => new()
        {
            ToolInvocationId = ToolInvocationId.New(), ToolId = "read_file", Succeeded = true,
            Images = [Image(id, incomingBytes)], ResultJson = "{\"source\":\"fixture\"}",
        };
        var original = Result("first");
        var first = ToolEvidenceAdmission.AdmitImages(original, reserved);
        var second = ToolEvidenceAdmission.AdmitImages(Result("second"), reserved);
        Assert.Equal(accepted, first.Images.Count);
        Assert.Empty(second.Images);
        Assert.Equal(count + accepted, reserved.Count);
        Assert.Equal(original.ResultJson, first.ResultJson);
        Assert.Single(original.Images);
        Assert.True(second.Succeeded);
        Assert.True(second.IsTruncated);
        Assert.Contains("aggregate-image-limit", second.ModelResultContent, StringComparison.Ordinal);
        Assert.Contains("second", second.ModelResultContent, StringComparison.Ordinal);
        Assert.Contains(original.ResultJson!, second.ModelResultContent, StringComparison.Ordinal);
    }
}
