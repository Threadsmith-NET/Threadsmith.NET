namespace Threadsmith.Architecture.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    private sealed class SkillCommandContinuationModel : IModelProvider
    {
        private readonly IModelProvider _inner;

        internal SkillCommandContinuationModel(string repository)
        {
            _inner = new FakeModelProvider(new ScriptedSession
            {
                Turns = [
                    new ScriptedTurn { ToolName = "run_process", ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git status", WorkingDirectory = repository }) },
                    new ScriptedTurn { ToolName = "run_process", ArgumentsJson = JsonSerializer.Serialize(new RunProcessInput { Command = "git diff", WorkingDirectory = repository }) },
                    new ScriptedTurn { Text = "{\"succeeded\":true}" },
                ],
            });
        }

        internal int Requests { get; private set; }

        public IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, CancellationToken cancellationToken = default)
        {
            Requests++;
            return _inner.StreamAsync(request, cancellationToken);
        }
    }
}
