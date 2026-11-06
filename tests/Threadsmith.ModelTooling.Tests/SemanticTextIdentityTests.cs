namespace Threadsmith.ModelTooling.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis.Text;
using Threadsmith.DotNet;
using Xunit;

/// <summary>Checks bounded decoded-text hashing against the existing identity format.</summary>
public static class SemanticTextIdentityTests
{
    /// <summary>Encoding chunks do not alter line endings, BOM characters, or surrogate fallback.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(4095)]
    [InlineData(8191)]
    [InlineData(65535)]
    public static async Task BoundedHashMatchesExistingUtf8IdentityAcrossSurrogateAndLineBoundaries(int prefixLength)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new string('x', prefixLength) + "\U0001F600\r\n\uFEFF\n\uD800end";
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        using var reader = new StringReader(content);

        Assert.Equal(expected, SemanticTextIdentity.Compute(SourceText.From(content, Encoding.Unicode), ct));
        Assert.Equal(expected, await SemanticTextIdentity.ComputeAsync(reader, ct));
    }

    /// <summary>Both text sources honor caller cancellation.</summary>
    [Fact]
    public static async Task BoundedHashHonorsCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        using var reader = new StringReader(new string('x', 8192));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SemanticTextIdentity.ComputeAsync(reader, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => SemanticTextIdentity.Compute(SourceText.From("source"), cancellation.Token));
    }
}
