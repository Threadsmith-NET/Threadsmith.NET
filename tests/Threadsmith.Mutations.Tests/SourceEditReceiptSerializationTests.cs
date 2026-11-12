namespace Threadsmith.Mutations.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Execution;
using Xunit;

/// <summary>Verifies exact serialization limits and bounded allocations for oversized evidence.</summary>
public sealed class SourceEditReceiptSerializationTests
{
    /// <summary>Receipts at the byte ceiling retain the original JSON, including escaped Unicode.</summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("<escaped>\"\\🙂")]
    public void ExactUtf8CeilingRetainsCompleteOriginalJson(string text)
    {
        var receipt = new SourceEditReceipt(Guid.NewGuid(), MutationSetId.New(), SourceEditStatus.Applied, [text], text);
        var expected = JsonSerializer.Serialize(receipt);
        var bytes = Encoding.UTF8.GetByteCount(expected);

        Assert.Equal(expected, new SourceEditReceiptSerialization(bytes).TrySerialize(receipt));
        Assert.Null(new SourceEditReceiptSerialization(bytes - 1).TrySerialize(receipt));
        Assert.Equal(expected, new SourceEditReceiptSerialization(bytes + 1).TrySerialize(receipt));
    }

    /// <summary>Allocation stays within a fixed budget for both large collections and individual tokens.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedEvidenceHasBoundedSerializationAllocations(bool singleLargeToken)
    {
        // Arrange outside the measured region: each receipt represents over 16 MiB of JSON.
        const int maximumBytes = 1024 * 1024;
        var diagnostic = new SourceEditDiagnostic("CS0246", new string('<', 1024), "Example.cs", 1, "Example", "net10.0", "introduced");
        var receipt = new SourceEditReceipt(Guid.NewGuid(), MutationSetId.New(), SourceEditStatus.Applied, ["Example.cs"], singleLargeToken ? new string('<', 16 * maximumBytes) : "Applied")
        {
            Analysis = new()
            {
                EffectId = Guid.NewGuid(),
                Diagnostics = singleLargeToken ? [] : Enumerable.Repeat(diagnostic, 20000).ToArray(),
            },
        };
        var serializer = new SourceEditReceiptSerialization(maximumBytes);
        Assert.Null(serializer.TrySerialize(receipt)); // Warm metadata and JSON escape buffers.

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = serializer.TrySerialize(receipt);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(result);
        Assert.InRange(allocated, 0, 8L * maximumBytes);
    }
}
