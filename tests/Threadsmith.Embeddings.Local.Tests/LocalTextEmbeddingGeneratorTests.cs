namespace Threadsmith.Embeddings.Local.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime.Tensors;
using Threadsmith.Core;
using Xunit;

/// <summary>Verifies local embedding behavior at its host-owned boundary.</summary>
public sealed class LocalTextEmbeddingGeneratorTests
{
    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static void PoolingRejectsInvalidOutput()
    {
        var tensor = new DenseTensor<float>([1, 1, 384]);
        Assert.Throws<TextEmbeddingUnavailableException>(() => MiniLmEmbedderEngine.PoolAndNormalize(tensor, [1]));
        tensor[0, 0, 1] = float.NaN;
        Assert.Throws<TextEmbeddingUnavailableException>(() => MiniLmEmbedderEngine.PoolAndNormalize(tensor, [1]));
        Assert.Throws<TextEmbeddingUnavailableException>(() => MiniLmEmbedderEngine.PoolAndNormalize(tensor, [1, 1]));
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static void CompleteInputBoundaryRetainsSepAndReportsOverflow()
    {
        using var vocabulary = new MemoryStream(Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\na\n"));
        var tokenizer = MiniLmEmbedderEngine.CreateTokenizer(vocabulary);
        // Compatibility with the supplied engine includes both the tokenizer's and
        // engine's boundary tokens. Count all four before truncation admission.
        var single = MiniLmEmbedderEngine.Encode(tokenizer, "a");
        Assert.Equal([2L, 2L, 5L, 3L, 3L], single.Ids);
        Assert.Equal(5, single.FullTokenCount);
        var exact = MiniLmEmbedderEngine.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 252)));
        var overflow = MiniLmEmbedderEngine.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 253)));
        Assert.Equal(256, exact.FullTokenCount);
        Assert.False(exact.WasTruncated);
        Assert.Equal(257, overflow.FullTokenCount);
        Assert.True(overflow.WasTruncated);
        Assert.Equal(256, overflow.Ids.Length);
        Assert.Equal(2, overflow.Ids[0]);
        Assert.Equal(3, overflow.Ids[^1]);
        Assert.All(overflow.AttentionMask, value => Assert.Equal(1, value));
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static async Task PreCancelledInputNeverLoadsAssets()
    {
        await using var generator = new LocalTextEmbeddingGenerator(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync("text", source.Token));
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static async Task MissingAssetsAreActionableAndDisposalIsTerminal()
    {
        var generator = new LocalTextEmbeddingGenerator(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await using (generator)
        {
            var error = await Assert.ThrowsAsync<TextEmbeddingUnavailableException>(() => generator.GenerateAsync("text", TestContext.Current.CancellationToken));
            Assert.Contains("Stage-EmbeddingAssets.ps1", error.Message, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => generator.GenerateAsync("text", TestContext.Current.CancellationToken));
    }
}
