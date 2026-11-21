namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
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

public sealed partial class SourceEditApplicationTests
{
    private static readonly JsonSerializerOptions RecoveryOutputOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private sealed class RecoveryProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public Guid EvidenceId { get; set; }

        public bool ApplyEdit { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count == 1)
            {
                yield return new() { Output = new ToolRequestModelOutput(ApplyEdit ? "edit_source" : "dotnet_build", ApplyEdit ? "{\"rationale\":\"Apply edit\",\"mutations\":[{\"type\":\"ReplaceText\",\"relativePath\":\"Example.cs\",\"expectedText\":\"Example\",\"replacementText\":\"Changed\"}]}" : "{\"targetPath\":\"Example.csproj\"}"), FinishReason = ModelFinishReason.ToolCalls };
            }
            else if (Requests.Count == 3 && request.Tools.Any(tool => tool.Name == ActiveTurnEvidenceTool.ToolId))
            {
                yield return new() { Output = new ToolRequestModelOutput(ActiveTurnEvidenceTool.ToolId, JsonSerializer.Serialize(new { evidenceId = EvidenceId })), FinishReason = ModelFinishReason.ToolCalls };
            }
            else
            {
                yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
