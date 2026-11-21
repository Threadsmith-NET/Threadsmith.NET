namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SessionConversationTests
{
    private sealed class InterruptedArchiveProvider : IModelProvider
    {
        private readonly string _completion;

        public InterruptedArchiveProvider(string completion)
        {
            _completion = completion;
        }

        public TaskCompletionSource PartialResponseReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ModelStreamRequest> Requests { get; } = [];

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (Requests.Count == 1)
            {
                yield return new ModelChunk { Text = "I inspected the source." };
                yield return new ModelChunk { Output = new ToolRequestModelOutput("list_files", "{\"path\":\".\"}") };
            }
            else if (Requests.Count == 2)
            {
                yield return new ModelChunk { Text = "The assessment is partly complete." };
                PartialResponseReady.TrySetResult();
                await ReleaseResponse.Task.WaitAsync(cancellationToken);
                if (_completion == "failure")
                {
                    throw new ModelProviderException("Simulated provider interruption.");
                }

                yield return new ModelChunk { Text = " Finished.", FinishReason = ModelFinishReason.Stop };
            }
            else
            {
                yield return new ModelChunk { Text = "Continuing with the saved assessment.", FinishReason = ModelFinishReason.Stop };
            }
        }
    }
}
