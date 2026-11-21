namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    private sealed class CommandContinuationModel : IModelProvider
    {
        private readonly IModelProvider _inner;
        private readonly Action _firstRequest;

        internal CommandContinuationModel(string firstArguments, string secondArguments, Action firstRequest)
        {
            _firstRequest = firstRequest;
            _inner = new FakeModelProvider(new ScriptedSession
            {
                Turns = [new ScriptedTurn { ToolName = "run_process", ArgumentsJson = firstArguments }, new ScriptedTurn { ToolName = "run_process", ArgumentsJson = secondArguments }, new ScriptedTurn { Text = "Done." }],
            });
        }

        internal int Requests { get; private set; }

        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
        {
            if (Requests++ == 0)
            {
                _firstRequest();
            }

            return _inner.StreamAsync(request, cancellationToken);
        }
    }
}
