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
