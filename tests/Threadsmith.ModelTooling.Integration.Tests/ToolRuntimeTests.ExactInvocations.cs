namespace Threadsmith.ModelTooling.Integration.Tests;

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public static partial class ToolRuntimeTests
{
    /// <summary>Verifies exact-call limits through the owning admission and configuration boundaries.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public static void ExactRuntimeOverride_SurvivesDynamicReplacementAndDefinitionRefresh(int configured)
    {
        var tool = new ChangingExactTool();
        var registry = new ToolRegistry([], runtimeOptions: new ToolRuntimeOptions
        {
            ByTool = [new ToolRuntimeToolOverride { ToolId = tool.Definition.Id, NumberOfExactToolInvocationsPerTurn = configured }],
        });
        var source = new ToolActivitySource(ToolActivitySourceKind.Mcp, "server");
        registry.RegisterOrReplace(tool, source);
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        tool.Refresh();
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        var replacement = new ChangingExactTool();
        registry.RegisterOrReplace(replacement, source, tool);
        Assert.Equal(configured, registry.Get(tool.Definition.Id).Definition.NumberOfExactToolInvocationsPerTurn);
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = Environment.CurrentDirectory,
            Fingerprint = "test",
            ResourceLimits = ImmutableDictionary<string, int>.Empty.Add("tools:runtime:byTool:server:search:NumberOfExactToolInvocationsPerTurn", 1),
        };
        var captured = ToolRuntimePolicy.ApplyCeiling(registry.Get(tool.Definition.Id), policy);
        Assert.Equal(Math.Min(configured, 1), captured.Definition.NumberOfExactToolInvocationsPerTurn);
        replacement.Refresh();
        Assert.Equal(Math.Min(configured, 1), captured.Definition.NumberOfExactToolInvocationsPerTurn);
    }

    private sealed class ChangingExactTool : Tool<DateTimeInput, DateTimeOutput>
    {
        private ToolDefinition _definition = new DateTimeTool(TestPromptLoader.Instance).Definition with { Id = "server:search", NumberOfExactToolInvocationsPerTurn = 3 };

        public override ToolDefinition Definition => _definition;

        public void Refresh() => _definition = _definition with { Description = "updated", NumberOfExactToolInvocationsPerTurn = 7 };

        public override Task<ToolExecution<DateTimeOutput>> ExecuteAsync(DateTimeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void ValidateInput(DateTimeInput input) => ArgumentNullException.ThrowIfNull(input);
    }

    private sealed class ExactInvocationModelProvider : IModelProvider
    {
        private readonly string _tool;
        private readonly string _arguments;
        private readonly int _attempts;
        private readonly bool _duplicateFirstBatch;

        public ExactInvocationModelProvider(string tool, string arguments, int attempts, bool duplicateFirstBatch)
        {
            _tool = tool;
            _arguments = arguments;
            _attempts = attempts;
            _duplicateFirstBatch = duplicateFirstBatch;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count <= _attempts + (_duplicateFirstBatch ? 1 : 0))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, _arguments), FinishReason = ModelFinishReason.ToolCalls };
                if (_duplicateFirstBatch && Requests.Count == 1)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, _arguments), FinishReason = ModelFinishReason.ToolCalls };
                }
            }
            else
            {
                yield return new ModelChunk { Text = "done", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
