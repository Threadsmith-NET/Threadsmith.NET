namespace Threadsmith.Reranking.Local.Tests;

using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Xunit;

/// <summary>Verifies local cross-encoder input boundaries and unavailable-runtime behavior.</summary>
public sealed class LocalTextCrossEncoderTests
{
    /// <summary>Verifies pair framing, segment assignment, and full input accounting without native assets.</summary>
    [Fact]
    public static void PairEncodingPreservesReferenceFrameAndReportsTrailingOverflow()
    {
        using var vocabulary = CreateVocabulary();
        var tokenizer = LocalTextCrossEncoderEngine.CreateTokenizer(vocabulary);
        var framed = LocalTextCrossEncoderEngine.Encode(tokenizer, "a", "a");
        var overflow = LocalTextCrossEncoderEngine.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 252)), "a");

        Assert.Equal(1L, framed.AttentionMask[0]);
        Assert.Equal(101L, framed.InputIds[0]);
        Assert.Equal(0L, framed.TokenTypeIds[0]);
        Assert.Equal(102L, framed.InputIds[3]);
        Assert.Equal(0L, framed.TokenTypeIds[3]);
        Assert.Equal(102L, framed.InputIds[4]);
        Assert.Equal(1L, framed.TokenTypeIds[4]);
        Assert.Equal(256, overflow.InputIds.Length);
        Assert.True(overflow.WasTruncated);
        Assert.True(overflow.InputTokenCount > overflow.InputIds.Length);
    }

    /// <summary>Verifies that pre-cancelled requests cannot trigger asset loading.</summary>
    [Fact]
    public static async Task PreCancelledInputNeverLoadsAssets()
    {
        await using var encoder = new LocalTextCrossEncoder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoder.ScoreAsync("query", ["document"], source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => encoder.ScoreAsync("query", [], source.Token));
    }

    /// <summary>Verifies request boundaries and terminal disposal without native assets.</summary>
    [Fact]
    public static async Task EmptyAndInvalidRequestsDoNotLoadAssets()
    {
        var encoder = new LocalTextCrossEncoder(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        await using (encoder)
        {
            var empty = await encoder.ScoreAsync("query", [], TestContext.Current.CancellationToken);
            Assert.Empty(empty);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => encoder.ScoreAsync("query", Enumerable.Repeat("document", 65).ToArray(), TestContext.Current.CancellationToken));
            var error = await Assert.ThrowsAsync<TextCrossEncoderUnavailableException>(() => encoder.ScoreAsync("query", ["document"], TestContext.Current.CancellationToken));
            Assert.Contains("cross-encoder asset", error.Message, StringComparison.Ordinal);
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(() => encoder.ScoreAsync("query", ["document"], TestContext.Current.CancellationToken));
    }

    /// <summary>Verifies true batch scoring, input order, token accounting, and finite logits against staged assets.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task StagedModelPreservesInputOrderAndBatchScores()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_RERANKING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_RERANKING_INTEGRATION=1 after explicit asset staging.");
        }

        await using var encoder = new LocalTextCrossEncoder();
        var documents = new[] { "a software deployment review", "a recipe for soup", string.Join(' ', Enumerable.Repeat("a", 300)) };

        var batch = await encoder.ScoreAsync("deployment review", documents, TestContext.Current.CancellationToken);
        var singles = await Task.WhenAll(documents.Select(document => encoder.ScoreAsync("deployment review", [document], TestContext.Current.CancellationToken)));

        Assert.Equal(documents.Length, batch.Count);
        Assert.All(batch, score => Assert.True(double.IsFinite(score.Score)));
        Assert.True(batch[2].WasTruncated);
        for (var index = 0; index < documents.Length; index++)
        {
            Assert.Equal(singles[index][0].InputTokenCount, batch[index].InputTokenCount);
            Assert.Equal(singles[index][0].WasTruncated, batch[index].WasTruncated);
            Assert.Equal(singles[index][0].Score, batch[index].Score, 5);
        }
    }

    /// <summary>Verifies raw logits against the unmodified production source's fixed-256 output fixture.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task StagedModelMatchesProductionReferenceLogits()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_RERANKING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_RERANKING_INTEGRATION=1 after explicit asset staging.");
        }

        await using var encoder = new LocalTextCrossEncoder();
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "production-reference-parity.json"),
            TestContext.Current.CancellationToken));
        var root = fixture.RootElement;
        var query = root.GetProperty("query").GetString() ?? throw new InvalidDataException("Reference query is missing.");
        var samples = root.GetProperty("samples").EnumerateArray().ToArray();
        var documents = samples.Select(sample => sample.GetProperty("document").GetString() ?? throw new InvalidDataException("Reference document is missing.")).ToArray();

        var actual = await encoder.ScoreAsync(query, documents, TestContext.Current.CancellationToken);

        Assert.Equal(samples.Length, actual.Count);
        for (var index = 0; index < samples.Length; index++)
        {
            Assert.Equal(samples[index].GetProperty("score").GetDouble(), actual[index].Score, 5);
        }
    }

    /// <summary>Verifies deterministic pre-run cancellation does not poison a subsequently queued inference.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task StagedModelCancellationPreservesQueuedInference()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_RERANKING_INTEGRATION") != "1")
        {
            Assert.Skip("Set THREADSMITH_RERANKING_INTEGRATION=1 after explicit asset staging.");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        await using var encoder = new LocalTextCrossEncoder(
            Path.Combine(AppContext.BaseDirectory, "crossencoders", "ms-marco-MiniLM-L6-v2"), 1, () =>
            {
                entered.TrySetResult();
                release.Wait();
            });
        using var source = new CancellationTokenSource();
        var cancelled = encoder.ScoreAsync("query", [string.Join(' ', Enumerable.Repeat("a", 252))], source.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queued = encoder.ScoreAsync("queue remains available", ["document"], TestContext.Current.CancellationToken);

        await source.CancelAsync();
        release.Set();

        try
        {
            await cancelled;
            Assert.Fail("Cancelled native inference completed successfully.");
        }
        catch (OperationCanceledException)
        {
            Assert.True(source.IsCancellationRequested);
        }

        var result = await queued.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Single(result);
        Assert.True(double.IsFinite(result[0].Score));
    }

    private static MemoryStream CreateVocabulary()
    {
        var tokens = Enumerable.Range(0, 105).Select(index => $"[unused{index}]").ToArray();
        tokens[0] = "[PAD]";
        tokens[1] = "[UNK]";
        tokens[101] = "[CLS]";
        tokens[102] = "[SEP]";
        tokens[103] = "[MASK]";
        tokens[104] = "a";
        return new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', tokens)));
    }
}
