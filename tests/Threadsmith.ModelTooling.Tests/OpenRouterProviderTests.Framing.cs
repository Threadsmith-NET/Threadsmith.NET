namespace Threadsmith.ModelTooling.Tests;

using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Threadsmith.Models;
using Xunit;

public static partial class OpenRouterProviderTests
{
    /// <summary>UTF-8, BOMs, multiline events and line terminators survive fragmented transport reads.</summary>
    [Theory]
    [InlineData(1, "\n")]
    [InlineData(3, "\r\n")]
    [InlineData(4096, "\r")]
    public static async Task FragmentedFrames_PreserveUnicodeAndParsedOwnership(int fragmentBytes, string newline)
    {
        // Arrange: real multibyte UTF-8, not JSON unicode escapes, crosses both encoder buffers.
        var value = new string('x', 1500) + " café 漢字 🎻 " + new string('y', 1500);
        var terminal = JsonNode.Parse(Completed(OutputMessage(value))[6..])!;
        var json = terminal.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var frame = "\uFEFF: comment" + newline + "event: response.completed" + newline
            + "data: " + json.Replace(",\"response\":", "," + newline + "data: \"response\":", StringComparison.Ordinal)
            + newline + newline + "data: [DONE]" + newline + newline;
        using var handler = new FragmentedFrameHandler(Encoding.UTF8.GetBytes(frame), fragmentBytes);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var chunks = new List<ModelChunk>();

        // Act: the completed DOM is inspected after the reusable buffer parses [DONE].
        await CollectAsync(Provider(client, Model()), Request() with { Tools = [] }, chunks);

        // Assert
        Assert.Equal(value, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.Single(chunks, chunk => chunk.Usage is not null);
        Assert.Single(chunks, chunk => chunk.FinishReason == ModelFinishReason.Stop);
    }

    private sealed class FragmentedFrameHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly int _fragmentBytes;

        public FragmentedFrameHandler(byte[] bytes, int fragmentBytes)
        {
            _bytes = bytes;
            _fragmentBytes = fragmentBytes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new FragmentedFrameStream(_bytes, _fragmentBytes)),
            });
        }
    }

    private sealed class FragmentedFrameStream : MemoryStream
    {
        private readonly int _fragmentBytes;

        public FragmentedFrameStream(byte[] bytes, int fragmentBytes)
            : base(bytes, writable: false)
        {
            _fragmentBytes = fragmentBytes;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, _fragmentBytes)], cancellationToken);
    }
}
