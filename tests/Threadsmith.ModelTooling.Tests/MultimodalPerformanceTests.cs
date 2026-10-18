namespace Threadsmith.ModelTooling.Tests;

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

public sealed partial class MultimodalTests
{
    /// <summary>Exercises production keyset cleanup across multiple batches, tied timestamps and owned rows.</summary>
    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task QuotaCleanupUsesOrderedBatches(int count)
    {
        await using var fixture = await Fixture.CreateAsync(maximumRetainedImageBytes: (count / 2) + Png.Length);
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.Root, "state.db")};Pooling=False");
        await connection.OpenAsync(token);
        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token))
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO artifacts VALUES($hash, 'image', 1, NULL, $hash || '.bin', '2026-01-01');";
            var hash = insert.Parameters.Add("$hash", SqliteType.Text);
            for (var index = 0; index < count; index++)
            {
                hash.Value = index.ToString("D64", System.Globalization.CultureInfo.InvariantCulture);
                await insert.ExecuteNonQueryAsync(token);
            }

            await using var protect = connection.CreateCommand();
            protect.Transaction = transaction;
            protect.CommandText = "INSERT INTO image_artifact_refs VALUES('owned', 'shared-session', $hash, '{}');";
            protect.Parameters.AddWithValue("$hash", new string('0', 64));
            await protect.ExecuteNonQueryAsync(token);
            await transaction.CommitAsync(token);
        }

        await using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT content_hash, length, recorded_at FROM artifacts a WHERE kind='image' AND (recorded_at, content_hash) > ('2025', '') AND NOT EXISTS (SELECT 1 FROM image_artifact_refs r WHERE r.content_hash = a.content_hash) ORDER BY recorded_at, content_hash LIMIT 128;";
        var details = new List<string>();
        await using (var reader = await plan.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                details.Add(reader.GetString(3));
            }
        }

        Assert.Contains(details, detail => detail.Contains("ix_artifacts_image_quota", StringComparison.Ordinal));
        Assert.DoesNotContain(details, detail => detail.Contains("TEMP B-TREE", StringComparison.Ordinal));
        var watch = Stopwatch.StartNew();
        await fixture.Store.StoreImageAsync(new MemoryStream(Png), SessionId.New(), "quota", cancellationToken: token);
        watch.Stop();
        var remaining = await fixture.Store.ListAsync(null, token);
        Assert.Equal((count / 2) + 1, remaining.Count);
        Assert.Contains(remaining, item => item.ContentHash == new string('0', 64));
        TestContext.Current.TestOutputHelper?.WriteLine($"Quota rows={count}; reclaimed={count / 2}; elapsed={watch.Elapsed.TotalMilliseconds:F1} ms (production deletion, missing-body fixtures).");
    }

    /// <summary>Serializes exact binary bytes once, including base64 padding, escaping and maximum request payloads.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3073, 1)]
    [InlineData(5242880, 4)]
    public async Task ChatImageUtf8PayloadIsExactAndReusedAcrossRetry(int byteLength, int imageCount)
    {
        var token = TestContext.Current.CancellationToken;
        var bytes = new byte[byteLength];

        // This pattern produces '+' and '/' throughout base64 and exercises the ASCII-only writer.
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(index % 3 == 0 ? 251 : 255);
        }

        var originals = bytes.ToArray();
        using var handler = new RetryImageHandler(bytes, originals, imageCount);
        using var client = new HttpClient(handler);
        var config = new OpenAiCompatibleProviderConfiguration
        {
            Id = "fixture", Name = "fixture", BaseUri = new Uri("https://example.test/v1/"),
            Models =
            [
                new OpenAiCompatibleModelConfiguration
                {
                    Id = ModelProfileId.New(), Name = "fixture", ModelId = "fixture", ContextWindow = 200000,
                    RetryMaxAttempts = 2, RetryDelayMilliseconds = 0,
                    MaximumOutputTokens = 4096, SupportedInputContentTypes = ModelContentTypes.Text | ModelContentTypes.Image,
                    SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                    Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = true },
                },
            ],
        };
        var catalog = new EffectiveModelProviderCatalog(new ModelProviderCatalogConfiguration { Providers = [config] }, new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]));
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) => Task.FromResult<string?>(null), images: new TransportImageStore(bytes));
        var images = Enumerable.Range(0, imageCount).Select(index => new ImageArtifact
        {
            Id = $"image-{index}", ContentHash = new string('a', 64), MediaType = "image/png",
            ByteLength = byteLength, Width = 2, Height = 2, Source = "transport-fixture",
        }).ToArray();
        var request = new ModelStreamRequest
        {
            RunId = RunId.New(), ImageSessionId = SessionId.New(), Input = "pixels",
            Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "image", Content = ModelImages.Append([new ModelContentPart { Content = "<tag> \"quoted\" \u263a" }], images) }],
        };
        var allocated = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        await foreach (var chunk in provider.StreamAsync(request, token))
        {
            Assert.Null(chunk.Output);
        }

        watch.Stop();
        Assert.Equal(2, handler.Hashes.Count);
        Assert.Equal(handler.Hashes[0], handler.Hashes[1]);
        Assert.Equal(handler.Lengths[0], handler.Lengths[1]);
        TestContext.Current.TestOutputHelper?.WriteLine($"Image input={byteLength * imageCount} bytes; UTF8 body={handler.Lengths[0]}; retries=1; elapsed={watch.Elapsed.TotalMilliseconds:F1} ms; process allocated delta={GC.GetTotalAllocatedBytes(true) - allocated} bytes (includes hash-only transport for large payload).");
    }

    private sealed class TransportImageStore : IImageArtifactStore
    {
        private readonly byte[] _bytes;

        public TransportImageStore(byte[] bytes) => _bytes = bytes;

        public Task<ReadOnlyMemory<byte>> ReadImageAsync(ImageArtifact artifact, SessionId sessionId, CancellationToken cancellationToken = default)
            => Task.FromResult<ReadOnlyMemory<byte>>(_bytes);
    }

    private sealed class RetryImageHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly byte[] _original;
        private readonly int _count;

        public RetryImageHandler(byte[] bytes, byte[] original, int count)
        {
            _bytes = bytes;
            _original = original;
            _count = count;
        }

        public List<string> Hashes { get; } = [];

        public List<long> Lengths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = Assert.IsType<ReadOnlyMemoryContent>(request.Content);
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            Lengths.Add(stream.Length);
            Hashes.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
            if (_bytes.Length < 10000)
            {
                stream.Position = 0;
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var parts = json.RootElement.GetProperty("messages")[0].GetProperty("content");
                Assert.Equal("<tag> \"quoted\" \u263a", parts[0].GetProperty("text").GetString());
                Assert.Equal(_count + 1, parts.GetArrayLength());
                foreach (var part in parts.EnumerateArray().Skip(1))
                {
                    var url = part.GetProperty("image_url").GetProperty("url").GetString()!;
                    Assert.Equal(_original, Convert.FromBase64String(url["data:image/png;base64,".Length..]));
                }
            }

            if (Hashes.Count == 1)
            {
                Array.Fill(_bytes, (byte)0);
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"choices\":[{\"delta\":{\"content\":\"done\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
            };
        }
    }
}
