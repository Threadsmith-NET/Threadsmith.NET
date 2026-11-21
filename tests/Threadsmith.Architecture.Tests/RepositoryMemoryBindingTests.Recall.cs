namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.RepositoryIntelligence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class RepositoryMemoryBindingTests
{
    private sealed class RecallEmbeddingGenerator : ITextEmbeddingGenerator
    {
        public TextEmbeddingModelDescriptor Model { get; } = new("fixture", 1, 256);

        public Task<TextEmbeddingResult> GenerateAsync(string text, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new TextEmbeddingResult(new float[] { 1 }, 1, false));
        }
    }
}
