namespace Threadsmith.ParallelAgents.Tests;

using System.Runtime.CompilerServices;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ModelExplorerAssignmentRunnerTests
{
    private sealed class ExactInvocationProvider : IModelProvider
    {
        private readonly string _tool;
        private readonly int _attempts;
        private readonly bool _duplicate;

        public ExactInvocationProvider(string tool, int attempts, bool duplicate)
        {
            _tool = tool;
            _attempts = attempts;
            _duplicate = duplicate;
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.Yield();
            if (Requests.Count <= _attempts + (_duplicate ? 1 : 0))
            {
                yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, "{}") };
                if (_duplicate && Requests.Count == 1)
                {
                    yield return new ModelChunk { Output = new ToolRequestModelOutput(_tool, "{}") };
                }
            }
            else
            {
                yield return new ModelChunk { Text = "done" };
            }
        }
    }
}
