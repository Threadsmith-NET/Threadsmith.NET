namespace Threadsmith.Reranking.Local.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Threadsmith.Core;
using Xunit;

/// <summary>Verifies local cross-encoder input boundaries and unavailable-runtime behavior.</summary>
public sealed class LocalTextCrossEncoderTests
{
    /// <summary>Verifies pair framing, segment assignment, and full input accounting without native assets.</summary>
    [Fact]
    public static void PairEncodingPreservesCanonicalFrameAndDocumentContent()
    {
        using var vocabulary = CreateVocabulary();
        var tokenizer = LocalTextCrossEncoderEngine.CreateTokenizer(vocabulary);
        var framed = LocalTextCrossEncoderEngine.Encode(tokenizer, "a", "a");
        var overflow = LocalTextCrossEncoderEngine.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 252)), "a");

        Assert.Equal(1L, framed.AttentionMask[0]);
        Assert.Equal(101L, framed.InputIds[0]);
        Assert.Equal(0L, framed.TokenTypeIds[0]);
        Assert.Equal([101L, 104L, 102L, 104L, 102L], framed.InputIds);
        Assert.Equal([0L, 0L, 0L, 1L, 1L], framed.TokenTypeIds);
        Assert.All(framed.AttentionMask, value => Assert.Equal(1, value));
        Assert.Equal(256, overflow.InputIds.Length);
        Assert.False(overflow.WasTruncated);
        Assert.Equal(104, overflow.InputIds[^2]);
        var otherDocument = LocalTextCrossEncoderEngine.Encode(tokenizer, string.Join(' ', Enumerable.Repeat("a", 252)), "b");
        Assert.False(overflow.InputIds.SequenceEqual(otherDocument.InputIds));
        var literal = LocalTextCrossEncoderEngine.Encode(tokenizer, "a [SEP] a", "a");
        Assert.All(literal.TokenTypeIds[..^2], value => Assert.Equal(0, value));
        var first = tokenizer.EncodeToIds("a [SEP] a", addSpecialTokens: false);
        var second = tokenizer.EncodeToIds("a", addSpecialTokens: false);
        Assert.Equal(tokenizer.BuildInputsWithSpecialTokens(first, second).Select(value => (long)value), literal.InputIds);
        Assert.Equal(tokenizer.CreateTokenTypeIdsFromSequences(first, second).Select(value => (long)value), literal.TokenTypeIds);
    }

    /// <summary>Both full texts survive up to the verified limit; overflow cannot silently discard either side.</summary>
    [Theory]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(511)]
    [InlineData(512)]
    [InlineData(513)]
    public static void PairBoundaryIsCompleteOrExplicitlyRejected(int length)
    {
        using var vocabulary = CreateVocabulary();
        var tokenizer = LocalTextCrossEncoderEngine.CreateTokenizer(vocabulary);
        var query = string.Join(' ', Enumerable.Repeat("a", 252));
        var document = string.Join(' ', Enumerable.Repeat("a", length - 255));
        if (length > 512)
        {
            var error = Assert.Throws<TextCrossEncoderInputException>(() => LocalTextCrossEncoderEngine.Encode(tokenizer, query, document));
            Assert.Equal(252, error.QueryTokens);
            Assert.Equal(length - 255, error.DocumentTokens);
            return;
        }

        var pair = LocalTextCrossEncoderEngine.Encode(tokenizer, query, document);
        Assert.Equal(length, pair.InputIds.Length);
        Assert.Equal(102, pair.InputIds[^1]);
        Assert.Equal(length - 255, pair.DocumentTokenCount);
        Assert.All(pair.TokenTypeIds[254..], value => Assert.Equal(1, value));
    }

    /// <summary>Recall bounds only the question, consistently across documents, while strict scoring rejects overflow.</summary>
    [Fact]
    public static void RecallQueryKeepsBothEndsAndCompleteDocuments()
    {
        using var vocabulary = CreateVocabulary();
        var tokenizer = LocalTextCrossEncoderEngine.CreateTokenizer(vocabulary);
        var query = "a " + string.Join(' ', Enumerable.Repeat("b", 600)) + " a";
        var document = string.Join(' ', Enumerable.Repeat("a", 254));
        Assert.Throws<TextCrossEncoderInputException>(() => LocalTextCrossEncoderEngine.Encode(tokenizer, query, document));
        var pair = LocalTextCrossEncoderEngine.Encode(tokenizer, query, document, useRecallQuery: true);
        var shortDocument = LocalTextCrossEncoderEngine.Encode(tokenizer, query, "a", useRecallQuery: true);
        Assert.Equal(602, pair.QueryTokenCount);
        Assert.Equal(128, pair.UsedQueryTokenCount);
        Assert.Equal(254, pair.DocumentTokenCount);
        Assert.Equal(385, pair.InputIds.Length);
        var withHistory = LocalTextCrossEncoderEngine.Encode(tokenizer, query, document, useRecallQuery: true, taskIntent: string.Join(' ', Enumerable.Repeat("b", 300)));
        Assert.Equal(pair.InputIds, withHistory.InputIds);
        var followup = LocalTextCrossEncoderEngine.Encode(tokenizer, "a", "a", useRecallQuery: true, taskIntent: "b");
        Assert.Equal(2, followup.UsedQueryTokenCount);
        Assert.False(pair.WasTruncated);
        Assert.Equal(104, pair.InputIds[1]);
        Assert.Equal(104, pair.InputIds[128]);
        Assert.Equal(pair.InputIds[..130], shortDocument.InputIds[..130]);
        Assert.All(pair.InputIds[130..^1], token => Assert.Equal(104, token));
        Assert.Throws<TextCrossEncoderInputException>(() => LocalTextCrossEncoderEngine.Encode(
            tokenizer, query, string.Join(' ', Enumerable.Repeat("a", 382)), useRecallQuery: true));
    }

    /// <summary>Tests the pinned export directly before relying on its declared 512-position capacity.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task PinnedExportAcceptsFullCapacityPairs()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_RERANKING_INTEGRATION") != "1")
        {
            Assert.Skip("Stage the pinned reranker and enable integration checks.");
        }

        var bytes = await File.ReadAllBytesAsync(
            Path.Combine(AppContext.BaseDirectory, "crossencoders", "ms-marco-MiniLM-L6-v2", "model.onnx"),
            TestContext.Current.CancellationToken);
        Assert.Equal("5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var options = new SessionOptions { IntraOpNumThreads = 1, InterOpNumThreads = 1 };
        using var session = new InferenceSession(bytes, options);
        foreach (var length in (int[])[256, 511, 512])
        {
            var ids = Enumerable.Repeat(104L, length).ToArray();
            ids[0] = 101;
            ids[length / 2] = 102;
            ids[^1] = 102;
            var segments = Enumerable.Range(0, length).Select(index => index > length / 2 ? 1L : 0L).ToArray();
            NamedOnnxValue[] inputs =
            [
                NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(ids, [1, length])),
                NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length])),
                NamedOnnxValue.CreateFromTensor("token_type_ids", new DenseTensor<long>(segments, [1, length])),
            ];
            using var runOptions = new RunOptions();
            await using var cancellation = TestContext.Current.CancellationToken.Register(() => runOptions.Terminate = true);
            using var output = session.Run(inputs, ["logits"], runOptions);
            Assert.True(float.IsFinite(Assert.Single(output.Single().AsTensor<float>().ToArray())));
        }
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
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => encoder.ScoreAsync("query", Enumerable.Repeat("document", encoder.Model.MaxBatchSize + 1).ToArray(), TestContext.Current.CancellationToken));
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
        var recall = await encoder.ScoreRecallAsync(new("deployment review"), documents, TestContext.Current.CancellationToken);
        Assert.Equal(batch, recall);

        Assert.Equal(documents.Length, batch.Count);
        Assert.All(batch, score => Assert.True(double.IsFinite(score.Score)));
        Assert.False(batch[2].WasTruncated);
        for (var index = 0; index < documents.Length; index++)
        {
            Assert.Equal(singles[index][0].InputTokenCount, batch[index].InputTokenCount);
            Assert.Equal(singles[index][0].WasTruncated, batch[index].WasTruncated);
            Assert.Equal(singles[index][0].Score, batch[index].Score, 5);
        }
    }

    /// <summary>Relevant document-tail evidence reaches inference even beyond the former pair boundary.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task StagedModelUsesDocumentTailAndComparesNearLimitMemories()
    {
        if (Environment.GetEnvironmentVariable("THREADSMITH_RERANKING_INTEGRATION") != "1")
        {
            Assert.Skip("Stage the pinned reranker and enable integration checks.");
        }

        await using var encoder = new LocalTextCrossEncoder();
        var prefix = string.Join(' ', Enumerable.Repeat("implementation", 250)) + ". ";
        var scores = await encoder.ScoreAsync(
            "Which component enforces policy when executing model tools?",
            [prefix + "ToolInvocationPipeline enforces policy and approval for all model tool execution.",
                prefix + "The kitchen recipe describes how to cook tomato soup."],
            TestContext.Current.CancellationToken);
        Assert.True(scores[0].Score > scores[1].Score);
        Assert.All(scores, score =>
        {
            Assert.True(score.InputTokenCount > 256);
            Assert.False(score.WasTruncated);
        });
        var memory = string.Join(' ', Enumerable.Repeat("a", 254));
        var full = Assert.Single(await encoder.ScoreAsync(memory, [memory], TestContext.Current.CancellationToken));
        Assert.Equal(511, full.InputTokenCount);
        Assert.Equal(254, full.QueryTokenCount);
        Assert.Equal(254, full.DocumentTokenCount);
        Assert.False(full.WasTruncated);
        await Assert.ThrowsAsync<TextCrossEncoderInputException>(() => encoder.ScoreAsync(
            memory, [memory + " a a"], TestContext.Current.CancellationToken));
        Assert.Single(await encoder.ScoreAsync("policy", ["tool policy"], TestContext.Current.CancellationToken));
    }

    /// <summary>Historical reference texts still rank the relevant memory first after canonical framing.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public static async Task StagedModelRanksRelevantHistoricalReferenceFirst()
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
        Assert.True(actual[0].Score > actual.Skip(1).Max(score => score.Score));
        Assert.All(actual, score => Assert.False(score.WasTruncated));
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
