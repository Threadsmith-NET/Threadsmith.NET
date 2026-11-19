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
