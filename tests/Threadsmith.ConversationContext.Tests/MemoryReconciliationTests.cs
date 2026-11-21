namespace Threadsmith.ConversationContext.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Xunit;

/// <summary>Behavioral coverage for MemoryReconciliationTests.</summary>
public static class MemoryReconciliationTests
{
    private static readonly RepositoryMemoryOptions Reconcile = new()
    {
        ReconciliationEnabled = true,

        MaxRepoMemoriesInContext = 0,
    };

    private sealed class InterleavedSearch : IRepositoryMemorySearch
    {
        private readonly IRepositoryMemorySearch _inner;
        private readonly Func<int, Task> _afterRead;

        public InterleavedSearch(IRepositoryMemorySearch inner, Func<int, Task> afterRead)
        {
            _inner = inner;
            _afterRead = afterRead;
        }

        public int Calls { get; private set; }

        public async Task<RepositoryMemoryRetrievalResult> SearchAsync(RepositoryMemorySearchRequest request, CancellationToken cancellationToken = default)
        {
            var result = await _inner.SearchAsync(request, cancellationToken);
            await _afterRead(++Calls);
            return result;
        }
    }
}

internal sealed class MemoryScoreEncoder : ITextCrossEncoder
{
    public TextCrossEncoderModelDescriptor Model { get; } = new("memory-behavior", 256, 64);

    public Func<string, string, TextCrossEncoderScore> Score { get; set; } = (_, _) => new(2, 12, false);

    public List<string> Documents { get; } = [];

    public Task<IReadOnlyList<TextCrossEncoderScore>> ScoreAsync(string query, IReadOnlyList<string> documents, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Documents.AddRange(documents);
        return Task.FromResult<IReadOnlyList<TextCrossEncoderScore>>(documents.Select(text => Score(query, text)).ToArray());
    }
}
