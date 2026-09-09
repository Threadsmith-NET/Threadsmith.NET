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
    public static void PoolingExcludesPaddingAndNormalizesEveryComponent()
    {
        var tensor = new DenseTensor<float>([1, 3, 384]);
        tensor[0, 0, 0] = 3;
        tensor[0, 1, 383] = 4;
        tensor[0, 2, 0] = 999;
        var result = LocalTextEmbeddingGenerator.PoolAndNormalize(tensor, [1, 1, 0]);
        Assert.Equal(384, result.Length);
        Assert.Equal(0.6f, result[0], 5);
        Assert.Equal(0.8f, result[383], 5);
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static void PoolingRejectsInvalidOutput()
    {
        var tensor = new DenseTensor<float>([1, 1, 384]);
        Assert.Throws<TextEmbeddingUnavailableException>(() => LocalTextEmbeddingGenerator.PoolAndNormalize(tensor, [1]));
        tensor[0, 0, 1] = float.NaN;
        Assert.Throws<TextEmbeddingUnavailableException>(() => LocalTextEmbeddingGenerator.PoolAndNormalize(tensor, [1]));
        Assert.Throws<TextEmbeddingUnavailableException>(() => LocalTextEmbeddingGenerator.PoolAndNormalize(tensor, [1, 1]));
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    public static void CompleteInputBoundaryRetainsSepAndReportsOverflow()
    {
        using var vocabulary = new MemoryStream(Encoding.UTF8.GetBytes("[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\na\n"));
        var tokenizer = LocalTextEmbeddingGenerator.CreateTokenizer(vocabulary);
        var exact = LocalTextEmbeddingGenerator.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 254)));
        var overflow = LocalTextEmbeddingGenerator.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 255)));
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

    /// <summary>Cancellation interrupts native work while a queued caller can subsequently use the same session.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task RealNativeCancellationPreservesQueuedInference()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_EMBEDDING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_EMBEDDING_INTEGRATION=1 after explicit model staging.");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var generator = new LocalTextEmbeddingGenerator(
            Path.Combine(AppContext.BaseDirectory, "embeddings", "all-MiniLM-L12-v2"), () => entered.TrySetResult());
        using var source = new CancellationTokenSource();
        var cancelled = generator.GenerateAsync(string.Join(' ', Enumerable.Repeat("a", 254)), source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = generator.GenerateAsync("queue remains available after cancellation", TestContext.Current.CancellationToken);
        await source.CancelAsync();
        try
        {
            await cancelled;
            Assert.Fail("Native inference completed after cancellation instead of returning a cancelled operation.");
        }
        catch (OperationCanceledException)
        {
            Assert.True(source.IsCancellationRequested);
        }

        var result = await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(384, result.Vector.Length);
        Assert.False(result.WasTruncated);
    }

    /// <summary>Verifies the declared embedding behavior without online model calls.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task RealModelMatchesPinnedReferenceTokensMasksVectorsAndDynamicPadding()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_EMBEDDING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_EMBEDDING_INTEGRATION=1 after explicit asset staging to run offline real-model parity.");
        }

        var assetRoot = Path.Combine(AppContext.BaseDirectory, "embeddings", "all-MiniLM-L12-v2");
        using var vocabulary = File.OpenRead(Path.Combine(assetRoot, "vocab.txt"));
        var tokenizer = LocalTextEmbeddingGenerator.CreateTokenizer(vocabulary);
        await using var generator = new LocalTextEmbeddingGenerator();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tokenizer-parity.json"), TestContext.Current.CancellationToken));
        foreach (var sample in fixture.RootElement.GetProperty("samples").EnumerateArray())
        {
            var text = sample.GetProperty("text").GetString() ?? string.Empty;
            var encoded = LocalTextEmbeddingGenerator.Encode(tokenizer, text);
            Assert.True(sample.GetProperty("ids").EnumerateArray().Select(item => item.GetInt64()).SequenceEqual(encoded.Ids), $"Token mismatch for {text}: {string.Join(',', encoded.Ids)}");
            Assert.Equal(sample.GetProperty("attentionMask").EnumerateArray().Select(item => item.GetInt64()), encoded.AttentionMask);
            var actual = await generator.GenerateAsync(text, TestContext.Current.CancellationToken);
            Assert.Equal(sample.GetProperty("fullTokenCount").GetInt32(), actual.InputTokenCount);
            Assert.Equal(sample.GetProperty("wasTruncated").GetBoolean(), actual.WasTruncated);
            var expected = sample.GetProperty("vector").EnumerateArray().Select(item => item.GetSingle()).ToArray();
            var maxError = actual.Vector.ToArray().Zip(expected, (a, b) => Math.Abs(a - b)).Max();
            Assert.True(maxError < 0.00005, $"Reference vector max error {maxError} exceeds CPU tolerance.");
            Assert.InRange(actual.Vector.ToArray().Sum(value => (double)value * value), 0.99999, 1.00001);
        }

        // Returned vectors remain detached after a subsequent inference reuses the native session.
        var first = await generator.GenerateAsync("detached result", TestContext.Current.CancellationToken);
        var before = first.Vector.ToArray();
        await generator.GenerateAsync("different subsequent input", TestContext.Current.CancellationToken);
        Assert.Equal(before, first.Vector.ToArray());
    }
}
