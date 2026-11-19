namespace Threadsmith.ConversationContext.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises the production conversation/tool/service path with deterministic model decisions.</summary>
public static class MemoryToolFlowTests
{
    private sealed class CollisionModel : IModelProvider
    {
        public const string Replacement = "Revised cancellation convention with iterator support";
        private readonly bool _distinct;
        private readonly RepositoryMemoryType _type;
        private readonly RepositoryMemoryId _original;

        public CollisionModel(bool distinct, RepositoryMemoryType type, RepositoryMemoryId original)
        {
            _distinct = distinct;
            _type = type;
            _original = original;
        }

        public int Requests { get; private set; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            Requests++;
            if (Requests == 1)
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput("memories", JsonSerializer.Serialize(new { action = "add", text = Replacement, memoryType = _type == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational" })) };
                yield break;
            }

            var result = request.Messages.Last(message => message.Role == ModelMessageRole.Tool).GetModelVisibleContent();
            using var document = JsonDocument.Parse(result);
            var body = document.RootElement;
            if (Requests == 2)
            {
                Assert.Equal("reconciliationRequired", body.GetProperty("Outcome").GetString());
                Assert.False(body.GetProperty("Added").GetBoolean());
                Assert.Empty(body.GetProperty("SearchDetails").GetProperty("Candidates").EnumerateArray());
                Assert.Equal(1, body.GetProperty("SearchDetails").GetProperty("CandidateEvidenceOmissions").GetInt32());
                var candidate = Assert.Single(body.GetProperty("Entries").EnumerateArray());
                Assert.Equal(_original.Value.ToString("D"), candidate.GetProperty("Id").GetString());
                Assert.Contains("Original cancellation", candidate.GetProperty("Text").GetString(), StringComparison.Ordinal);
                Assert.Contains("expectedRevision", body.GetProperty("Resolution").GetString(), StringComparison.Ordinal);
                var arguments = _distinct
                    ? JsonSerializer.Serialize(new { action = "add", text = Replacement, memoryType = _type == RepositoryMemoryType.StandingPreference ? "standingPreference" : "situational", confirmDistinctFrom = new[] { new { id = _original.Value.ToString("D"), revision = candidate.GetProperty("Revision").GetInt64() } } })
                    : JsonSerializer.Serialize(new { action = "update", id = _original.Value.ToString("D"), text = Replacement, expectedRevision = candidate.GetProperty("Revision").GetInt64() });
                yield return new ModelChunk { Output = new ToolRequestModelOutput("memories", arguments) };
                yield break;
            }

            Assert.Equal(_distinct ? "added" : "updated", body.GetProperty("Outcome").GetString());
            yield return new ModelChunk { Text = "Memory resolved." };
        }
    }
}
