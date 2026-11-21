namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

internal static class MemoryTestData
{
    internal const string Repository = "test:repository";

    internal static RepositoryMemoryOperationRequest Operation(string action, string? text = null, RepositoryMemoryId? id = null)
    {
        return new()
        {
            RepositoryIdentity = Repository,
            Action = action,
            Id = id,
            Text = text,
            Origin = RepositoryMemoryOrigin.Manual,
        };
    }
}

internal sealed class TestMemoryEmbeddingGenerator : ITextEmbeddingGenerator
{
    public TextEmbeddingModelDescriptor Model { get; set; } = new("test-space", 3, 256);

    internal int Calls { get; private set; }

    internal Func<string, TextEmbeddingResult> Generate { get; set; } = _ => new TextEmbeddingResult(new float[] { 1, 0, 0 }, 3, false);

    public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(Generate(text));
    }
}
